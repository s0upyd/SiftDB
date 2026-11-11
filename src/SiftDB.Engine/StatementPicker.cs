using SiftDB.Abstractions;

namespace SiftDB.Engine;

public enum PickReason
{
    /// <summary>Nothing sensible to run: no statements, or the cursor is not in or right after one.</summary>
    None,
    /// <summary>The user selected text, so that is what runs.</summary>
    Selection,
    /// <summary>The cursor is inside a statement (or touching its end).</summary>
    CursorStatement,
    /// <summary>The cursor is on blank space (or just past the terminator) right below a statement.</summary>
    StatementAbove,
    /// <summary>The sheet holds a single statement, so it runs wherever the cursor is.</summary>
    OnlyStatement,
}

public readonly record struct StatementPick(SqlRange? Range, PickReason Reason);

/// <summary>
/// Decides what F5 / Ctrl+Enter runs (design section 6), in priority order: selected text; the statement the cursor is
/// in (if the cursor is on a blank line, the statement above; if nothing sensible is close by, nothing); the only
/// statement in the sheet. Pure and dialect-neutral: the statement ranges come from the provider's parser.
/// </summary>
public static class StatementPicker
{
    /// <param name="selectionStart">UTF-16 offset of the selection start (equal to <paramref name="selectionEnd"/> for a bare cursor).</param>
    /// <param name="selectionEnd">UTF-16 offset of the selection end, which is the cursor when nothing is selected.</param>
    public static StatementPick Pick(string text, IReadOnlyList<SqlRange> statements, int selectionStart, int selectionEnd)
    {
        var lo = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var hi = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);

        // 1. Selected text, trimmed of surrounding whitespace. Whitespace-only selections don't count.
        if (hi > lo)
        {
            var (s, e) = (lo, hi);
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > s) return new StatementPick(new SqlRange(s, e - s), PickReason.Selection);
        }

        if (statements.Count == 0) return new StatementPick(null, PickReason.None);
        var cursor = hi;

        // 2a. The cursor is inside a statement, or touching its end.
        foreach (var st in statements)
            if (cursor >= st.Start && cursor <= st.End)
                return new StatementPick(st, PickReason.CursorStatement);

        // 2b. Blank space (and at most the terminator) between the end of a statement and the cursor.
        SqlRange? above = null;
        foreach (var st in statements)
            if (st.End <= cursor) above = st; else break;
        if (above is { } a && IsOnlyBlankOrTerminator(text.AsSpan(a.End, cursor - a.End)))
            return new StatementPick(a, PickReason.StatementAbove);

        // 3. A sheet with one statement runs it from anywhere.
        return statements.Count == 1
            ? new StatementPick(statements[0], PickReason.OnlyStatement)
            : new StatementPick(null, PickReason.None);
    }

    private static bool IsOnlyBlankOrTerminator(ReadOnlySpan<char> gap)
    {
        var terminators = 0;
        foreach (var c in gap)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c == ';' && ++terminators == 1) continue;
            return false;
        }
        return true;
    }
}
