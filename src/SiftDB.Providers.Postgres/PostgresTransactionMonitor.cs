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
internal sealed class PostgresTransactionMonitor(NpgsqlConnection conn) : ITransactionMonitor
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

    private async Task Run(string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TransactionSnapshot> GetStateAsync(CancellationToken ct)
    {
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

    private async Task<TransactionSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand($"select {_xidFunction} is not null, transaction_timestamp()", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
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
