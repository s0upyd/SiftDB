using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>How a sheet treats identifier problems (design section 3).</summary>
public enum QueryMode
{
    /// <summary>The text you write is the text that runs. Problems are squiggles with quick fixes, and running is blocked
    /// while any remain.</summary>
    Strict,
    /// <summary>You write loosely and the editor shows what will run: quotes are added for you, and what executes is
    /// exactly what is displayed. Only an ambiguity (which needs a choice) blocks running.</summary>
    Assisted,
}

/// <param name="Sql">What to send to the database.</param>
/// <param name="Blocked">Why it must not run yet, or null.</param>
public sealed record PreparedRun(string Sql, string? Blocked);

public static class RunPreparer
{
    public static QueryMode ParseMode(string? mode) =>
        string.Equals(mode, "strict", StringComparison.OrdinalIgnoreCase) ? QueryMode.Strict : QueryMode.Assisted;

    /// <param name="range">The part of <paramref name="text"/> about to run (a statement, or a selection).</param>
    public static PreparedRun Prepare(string text, SqlRange range, ResolverResult resolved, QueryMode mode)
    {
        bool Inside(int start, int length) => start >= range.Start && start + length <= range.End;
        var corrections = resolved.Corrections.Where(c => Inside(c.Start, c.Length)).OrderBy(c => c.Start).ToList();
        var issues = resolved.Issues.Where(i => Inside(i.Start, i.Length)).ToList();
        var raw = text.Substring(range.Start, range.Length);

        if (mode == QueryMode.Strict)
        {
            // An unknown column is only a hint (the server has the last word, and a wrong guess must not stop valid SQL), so it
            // is shown but never holds up a run.
            var blocking = issues.Where(i => i.Code != IssueCode.UnknownColumn).ToList();
            var count = corrections.Count + blocking.Count;
            if (count == 0) return new PreparedRun(raw, null);
            var first = (corrections.Count > 0 ? corrections[0].Message : blocking[0].Message).TrimEnd('.'); // the sentence below adds its own
            return new PreparedRun(raw,
                $"{count} identifier problem{(count == 1 ? "" : "s")} to fix first. {first}. Use the quick fixes, or switch to Assisted mode.");
        }

        // Assisted: nothing is filled in for an ambiguity, so it has to be settled before the query runs. A qualified SET
        // column is certain to fail, with a server message that does not say why, so it is stopped here with one that does.
        var stopper = issues.FirstOrDefault(i => i.Code is IssueCode.Ambiguous or IssueCode.QualifiedSetColumn);
        if (stopper is not null) return new PreparedRun(raw, stopper.Message);

        var sql = raw;
        foreach (var c in corrections.OrderByDescending(c => c.Start))
            sql = sql.Remove(c.Start - range.Start, c.Length).Insert(c.Start - range.Start, c.Text);
        return new PreparedRun(sql, null);
    }
}
