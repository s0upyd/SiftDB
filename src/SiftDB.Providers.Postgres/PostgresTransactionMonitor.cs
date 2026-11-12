using System.Reflection;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Postgres transaction state, taken from the server.
///
/// Whether a transaction is open, or has failed, is the status byte the server sends with every ReadyForQuery. Npgsql
/// keeps it on an internal connector object and exposes no public way to read it, so it is read by reflection here and
/// nowhere else. If a future Npgsql moves it, <see cref="ProtocolStatusAvailable"/> turns false and this fails loudly
/// (there is a test for exactly that) instead of guessing. Timestamp tricks do not work: Npgsql's extended protocol
/// refreshes <c>statement_timestamp()</c> on every protocol message, so it differs from <c>transaction_timestamp()</c>
/// even in autocommit.
///
/// Whether the open transaction has written anything is a read-only SQL probe (an assigned xid). It runs only inside an
/// open transaction block, never assigns an xid, and so cannot change the state it measures.
/// </summary>
internal sealed class PostgresTransactionMonitor(NpgsqlConnection conn, PostgresSettings settings) : ITransactionMonitor
{
    private static readonly PropertyInfo? ConnectorProperty = typeof(NpgsqlConnection)
        .GetProperty("Connector", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly PropertyInfo? StatusProperty = ConnectorProperty?.PropertyType
        .GetProperty("TransactionStatus", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>False if this Npgsql no longer offers the protocol status.</summary>
    public static bool ProtocolStatusAvailable => ConnectorProperty is not null && StatusProperty is not null;

    private const string UndefinedFunction = "42883";
    private string _xidFunction = "pg_current_xact_id_if_assigned()"; // Postgres 13+; older servers use txid_current_if_assigned()

    public bool CanDetectWrites => true;

    public Task BeginAsync(CancellationToken ct) => Run("begin", ct);
    public Task CommitAsync(CancellationToken ct) => Run("commit", ct);
    public Task RollbackAsync(CancellationToken ct) => Run("rollback", ct);

    /// <summary>The savepoint that guards each statement. Released (or rolled back to) as soon as the statement is over.</summary>
    private const string Guard = "siftdb_statement";

    /// <summary>
    /// Any error aborts a Postgres transaction. With the setting on (the default), a statement inside a transaction runs
    /// behind a savepoint, so a failure undoes just that statement (design section 7), as psql's ON_ERROR_ROLLBACK does.
    /// Not guarded: statements outside a transaction (nothing to protect), a transaction that has already failed, and
    /// statements that control the transaction themselves (BEGIN, COMMIT, ROLLBACK, SAVEPOINT…), which must act on the
    /// user's transaction, not ours. SQL that does not parse is guarded: a typo is exactly what this is for.
    /// </summary>
    public async Task<bool> BeforeStatementAsync(string sql, CancellationToken ct)
    {
        if (!settings.UndoFailedStatement || ReadProtocolStatus() != "InTransactionBlock") return false;
        if (PostgresTransactionRules.ControlsTransaction(sql)) return false;
        await Run($"savepoint {Guard}", ct);
        return true;
    }

    /// <summary>
    /// A failed statement left the transaction aborted: roll back to the guard, which undoes the statement and nothing
    /// before it. Otherwise just release the guard. The protocol status decides, not <paramref name="failed"/>: a
    /// cancelled SELECT that streamed fine has nothing to undo.
    ///
    /// The SQL is not sent here but held, and goes out at the front of the next command, which is always the state check
    /// that follows a statement: one round trip instead of two. Every command this monitor sends flushes it first, so
    /// nothing (a Commit, say) can ever run with the guard still in place.
    /// </summary>
    public Task<bool> AfterStatementAsync(bool failed, CancellationToken ct)
    {
        switch (ReadProtocolStatus())
        {
            case "InFailedTransactionBlock":
                _pending = $"rollback to savepoint {Guard}; release savepoint {Guard}";
                return Task.FromResult(true);
            case "InTransactionBlock":
                _pending = $"release savepoint {Guard}";
                return Task.FromResult(false);
            default:
                return Task.FromResult(false); // the transaction is over (a function cannot end it, but be safe): no guard left
        }
    }

    /// <summary>The end of the last statement's guard, not sent yet: it rides along with the next command.</summary>
    private string? _pending;

    /// <summary>Hands over the held guard SQL, if any, to go in front of a command.</summary>
    private string TakePending(string sql)
    {
        var pending = _pending;
        _pending = null;
        return pending is null ? sql : $"{pending}; {sql}";
    }

    private async Task Run(string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(TakePending(sql), conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TransactionSnapshot> GetStateAsync(CancellationToken ct)
    {
        // A held guard ending changes the state (a rollback to the guard leaves the transaction open again), so it goes first.
        if (_pending is not null)
        {
            // Sent with the probe once the probe is known to work here: if the probe failed in the same command, the
            // failure would abort the transaction just rescued.
            if (_xidFunctionKnown) return await ProbeAsync(ct);
            await Run("select 1", ct);
        }
        switch (ReadProtocolStatus())
        {
            case "Idle": return new TransactionSnapshot(TransactionState.None, null);
            case "InFailedTransactionBlock": return new TransactionSnapshot(TransactionState.Failed, null);
            case "InTransactionBlock": return await ProbeAsync(ct);
            default:
                // "Pending": a command is still in flight, so the answer is not known yet. Callers ask only when idle.
                throw new InvalidOperationException("The session is busy; its transaction state is not known yet.");
        }
    }

    private string ReadProtocolStatus()
    {
        if (!ProtocolStatusAvailable)
            throw new NotSupportedException(
                "This version of Npgsql no longer exposes the protocol transaction status the transaction bar relies on. " +
                "See PostgresTransactionMonitor.");
        var connector = ConnectorProperty!.GetValue(conn)
            ?? throw new InvalidOperationException("The connection is not open.");
        return StatusProperty!.GetValue(connector)?.ToString() ?? "";
    }

    /// <summary>Set once a probe has worked: from then on a held guard ending can share its command.</summary>
    private bool _xidFunctionKnown;

    private async Task<TransactionSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(TakePending($"select {_xidFunction} is not null, transaction_timestamp()"), conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (r.FieldCount == 0 && await r.NextResultAsync(ct)) { } // past the guard ending's results, to the probe's
            await r.ReadAsync(ct);
            _xidFunctionKnown = true;
            return new TransactionSnapshot(
                r.GetBoolean(0) ? TransactionState.Changed : TransactionState.Open,
                new DateTimeOffset(r.GetFieldValue<DateTime>(1).ToUniversalTime()));
        }
        catch (PostgresException e) when (e.SqlState == UndefinedFunction && _xidFunction.StartsWith("pg_current", StringComparison.Ordinal))
        {
            _xidFunction = "txid_current_if_assigned()";
            return await ProbeAsync(ct);
        }
    }
}
