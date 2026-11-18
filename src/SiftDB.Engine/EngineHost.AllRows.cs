using System.Globalization;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// The missing-WHERE warning (design section 14, F1): an UPDATE or DELETE with no WHERE, or a TRUNCATE, is not run until
/// the user confirms it. The provider finds such statements in the parse tree and describes the table (temporary? about
/// how many rows?); the engine decides and words the warning, so every front end asks the same question.
/// </summary>
public sealed partial class EngineHost
{
    /// <summary>
    /// "checkRun": the UI asks before every run, and shows the warning instead of running when there is one. Answered with
    /// <c>runCheck</c>, whose <c>allRows</c> is the warning or null.
    /// </summary>
    private async Task CheckRunAsync(int id, int connectionId, string sql)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        string? warning = null;
        if (WholeTableChanges(state, sql) is { Count: > 0 } changes)
        {
            // The table is looked up on the session only while it is idle: a running statement is not held up to ask.
            var idle = state.SessionLock.Wait(0);
            try { warning = await AllRowsWarningAsync(state, changes, canAsk: idle); }
            finally { if (idle) state.SessionLock.Release(); }
        }
        Event(new { type = "runCheck", replyTo = id, allRows = warning });
    }

    /// <summary>
    /// None when the setting is off, or on a read-only session (the server refuses the change anyway), or in an aborted
    /// transaction (it cannot run until the rollback).
    /// </summary>
    private IReadOnlyList<WholeTableChange> WholeTableChanges(ConnectionState state, string sql) =>
        !CurrentSettings.WarnAllRows || state.ReadOnly || state.Tracker.State == TransactionState.Failed
            ? [] : state.Provider.ChangesEveryRow(sql);

    /// <summary>The warning, or null when every table involved is temporary. <paramref name="canAsk"/>: the caller holds the session.</summary>
    private static async Task<string?> AllRowsWarningAsync(ConnectionState state, IReadOnlyList<WholeTableChange> changes, bool canAsk)
    {
        var shown = new List<(WholeTableChange Change, long? Rows)>();
        foreach (var c in changes)
        {
            var facts = canAsk ? await state.Connection.DescribeTableAsync(c.Schema, c.Table, CancellationToken.None) : null;
            if (facts is { Temporary: true }) continue; // only this session sees it, and it goes when the session does
            // An estimate of 0 is as likely "never analysed" as "empty": say "every row" rather than "about 0".
            shown.Add((c, facts?.EstimatedRows is > 0 ? facts.EstimatedRows : null));
        }
        if (shown.Count == 0) return null;
        var inTransaction = state.Tracker.State != TransactionState.None || state.AutoTransaction;
        return AllRowsMessage(shown, inTransaction);
    }

    internal static string AllRowsMessage(IReadOnlyList<(WholeTableChange Change, long? Rows)> shown, bool inTransaction)
    {
        static string Name(WholeTableChange c) => c.Schema is null ? c.Table : $"{c.Schema}.{c.Table}";
        static string About(long? rows) => rows is { } n ? $" (about {FormatRows(n)} row{(n == 1 ? "" : "s")})" : "";

        string what;
        if (shown.Count == 1)
        {
            var (c, rows) = shown[0];
            what = c.Action switch
            {
                WholeTableAction.Update => $"This UPDATE has no WHERE, so it changes every row in {Name(c)}{About(rows)}.",
                WholeTableAction.Delete => $"This DELETE has no WHERE, so it deletes every row in {Name(c)}{About(rows)}.",
                _ => $"This TRUNCATE empties {Name(c)}{About(rows)}.",
            };
        }
        else
        {
            var tables = shown.GroupBy(s => Name(s.Change)).Select(g => g.Key + About(g.First().Rows)).ToList();
            what = tables.Count == 1
                ? $"This changes every row in {tables[0]}, more than once."
                : $"This changes every row in {tables.Count} tables: {string.Join(", ", tables)}.";
        }
        return what + (inTransaction
            ? " It runs inside the open transaction, so Rollback can still undo it."
            : " No transaction is open, so it cannot be undone.");
    }

    /// <summary>"950", "12,400", "45.3k", "1.2M": an estimate needs no more digits than that.</summary>
    internal static string FormatRows(long n)
    {
        var c = CultureInfo.InvariantCulture;
        return n switch
        {
            < 100_000 => n.ToString("N0", c),
            < 1_000_000 => (n / 1_000.0).ToString("0.#", c) + "k",
            < 1_000_000_000 => (n / 1_000_000.0).ToString("0.#", c) + "M",
            _ => (n / 1_000_000_000.0).ToString("0.#", c) + "B",
        };
    }
}
