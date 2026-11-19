using System.Text.Json;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// Visual EXPLAIN (design section 14, F6). The plan is asked for on the sheet's session, inside a dry run's savepoint (or
/// a transaction of its own) that is then rolled back: EXPLAIN ANALYZE really runs the statement, a write included, and
/// even a plain EXPLAIN that fails must not abort the user's transaction. The answer is one "plan" event: plans are small.
/// </summary>
public sealed partial class EngineHost
{
    /// <param name="root">"sql" is what to explain (as it would run); "source" and "sourceStart" the same statement as
    /// written in the editor, for linking plan steps to its table references.</param>
    private async Task ExplainAsync(int id, int connectionId, JsonElement root)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var sql = root.GetProperty("sql").GetString()!;
        var analyze = root.TryGetProperty("analyze", out var a) && a.ValueKind == JsonValueKind.True;
        var source = OptionalString(root, "source");
        var sourceStart = root.TryGetProperty("sourceStart", out var ss) && ss.ValueKind == JsonValueKind.Number ? ss.GetInt32() : 0;

        if (state.Connection.Plans is not { } plans) { Error(id, "Query plans are not available for this database."); return; }
        if (StatementSplitter.For(state.Provider.Parser).Split(sql).Count != 1) { Error(id, "EXPLAIN works on one statement at a time."); return; }
        var dry = state.Connection.DryRun;
        if (analyze && dry is null)
        {
            Error(id, "EXPLAIN ANALYZE runs the statement, and this database offers no way to roll it back here.");
            return;
        }

        CancelCurrent(state); // like a run, a plan replaces whatever the sheet was doing
        await state.SessionLock.WaitAsync();
        try
        {
            if (state.Active is { } previous) _queries.TryRemove(previous.Id, out _);
            if (state.Tracker.State == TransactionState.Failed) { Error(id, "The transaction has failed. Roll back before asking for a plan."); return; }
            var wasUnchanged = state.Tracker.State == TransactionState.Open;
            if (dry is not null) await dry.BeginAsync(CancellationToken.None);

            var cts = new CancellationTokenSource();
            state.Starting = cts;
            QueryPlan plan;
            try
            {
                plan = await plans.ExplainAsync(sql, analyze, cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                Event(new { type = "cancelled", replyTo = id });
                return;
            }
            finally
            {
                state.Starting = null;
                if (dry is not null) await EndDryRunAsync(state, dry, wasUnchanged);
                else await ObserveTransactionAsync(state, statement: false, rowsAffected: -1);
            }

            var report = PlanReporter.Build(plan, state.Provider.Parser, state.Provider.Identifiers, state.Schema, source, sourceStart);
            Event(new { type = "plan", replyTo = id, plan = report });
        }
        finally
        {
            state.SessionLock.Release();
        }
    }
}
