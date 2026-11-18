using System.Data.Common;
using System.Diagnostics;
using SiftDB.Abstractions;
using SiftDB.Bridge;

namespace SiftDB.Engine;

/// <summary>
/// Dry runs (design section 14, F2): an UPDATE or DELETE runs for real on the sheet's session, returns the rows it
/// touches, and is rolled back, leaving the session as it was. The result streams to the UI like any other, marked as a
/// dry run with how its columns are laid out.
/// </summary>
public sealed partial class EngineHost
{
    /// <summary>Rows a dry run shows at most. The count of every affected row still comes back.</summary>
    public const int DryRunMaxRows = 10_000;

    /// <summary>Why <paramref name="sql"/> cannot be dry-run on this session, or null with the plan to run.</summary>
    private static string? DryRunRefusal(ConnectionState state, string sql, out DryRunPlan plan)
    {
        plan = DryRunPlan.No("");
        if (state.ReadOnly) return "Not on a read-only connection: nothing can change there, so there is nothing to try.";
        if (state.Tracker.State == TransactionState.Failed) return "The transaction has failed. Roll back before a dry run.";
        if (state.Connection.DryRun is not { } dry) return "Dry runs are not available for this database.";
        plan = dry.Plan(sql);
        return plan.Refused;
    }

    private async Task DryRunAsync(int id, int connectionId, string sql)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        if (DryRunRefusal(state, sql, out var plan) is { } refused) { Error(id, refused); return; }
        var dry = state.Connection.DryRun!;
        CancelCurrent(state); // like a run, a dry run replaces whatever the sheet was doing
        await state.SessionLock.WaitAsync();
        var handedOver = false;
        try
        {
            if (state.Active is { } previous) _queries.TryRemove(previous.Id, out _);
            if (state.Tracker.State == TransactionState.Failed) { Error(id, "The transaction has failed. Roll back before a dry run."); return; }
            // An open transaction with nothing in it stays that way. Postgres gives the whole transaction an id as soon as
            // the savepoint writes, rolled back or not, so the server alone would say "changed" afterwards.
            var wasUnchanged = state.Tracker.State == TransactionState.Open;
            await dry.BeginAsync(CancellationToken.None);

            var sw = Stopwatch.StartNew();
            var cts = new CancellationTokenSource();
            state.Starting = cts;
            DbDataReader reader;
            try
            {
                reader = await state.Connection.ExecuteReaderAsync(plan.Sql, cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                state.Starting = null;
                await EndDryRunAsync(state, dry, wasUnchanged);
                Event(new { type = "cancelled", replyTo = id });
                return;
            }
            catch
            {
                state.Starting = null;
                await EndDryRunAsync(state, dry, wasUnchanged);
                throw;
            }
            state.Starting = null;

            var writer = new RowBatchWriter(reader);
            var queryId = Interlocked.Increment(ref _nextQuery);
            var session = new QuerySession(queryId, reader, writer, cts, sw, async (_, _) =>
            {
                try { await EndDryRunAsync(state, dry, wasUnchanged); }
                finally { state.SessionLock.Release(); }
            }, DryRunMaxRows);
            handedOver = true;
            _queries[queryId] = session;
            state.Active = session;

            var count = writer.Columns.Count;
            Event(new
            {
                type = "columns", replyTo = id, queryId,
                columns = writer.Columns.Select((c, i) => new { name = c.Name, kind = (int)c.Kind, type = GenericTypeOf(state.Provider, reader, i) }),
                dryRun = new
                {
                    shape = plan.Shape switch
                    {
                        DryRunShape.BeforeAndAfter => "beforeAndAfter",
                        DryRunShape.AfterOnly => "afterOnly",
                        _ => "deleted",
                    },
                    // Before and after: the first half of the columns is before, the second half after.
                    width = plan.Shape == DryRunShape.BeforeAndAfter ? count / 2 : count,
                    maxRows = DryRunMaxRows,
                },
            });
            session.Start();
        }
        finally
        {
            if (!handedOver) state.SessionLock.Release();
        }
    }

    /// <summary>
    /// Rolls the dry run back and reports where the session stands. If the rollback itself fails, the changes may still
    /// be in the transaction: that is said loudly, because the whole point was that nothing changes.
    /// </summary>
    private async Task EndDryRunAsync(ConnectionState state, IDryRunSupport dry, bool keepUnchanged)
    {
        try { await dry.EndAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            Event(new
            {
                type = "transactionUnknown", connectionId = state.Id,
                message = $"The dry run could not be undone: {ex.Message} Roll back the transaction to be sure nothing changed.",
            });
            return;
        }
        await ObserveTransactionAsync(state, statement: false, rowsAffected: -1, keepUnchanged: keepUnchanged);
    }
}
