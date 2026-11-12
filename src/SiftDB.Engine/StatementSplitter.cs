using System.Collections.Concurrent;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// The provider's statement split, plus blank lines as statement ends (design section 6). People often leave out the
/// <c>;</c> between statements they keep apart with a blank line. A blank line ends a statement only when the text
/// before it parses on its own and the next line starts a new statement, so a query with blank lines inside it (a CTE,
/// a long WHERE, a UNION) stays whole. Provider-neutral: it only uses the dialect's tokens and grammar check.
/// </summary>
public sealed class StatementSplitter(ISqlParser inner) : ISqlParser
{
    /// <summary>Words that start a statement. A line starting with anything else always continues the one above.</summary>
    private static readonly HashSet<string> Starters = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "with", "insert", "update", "delete", "merge", "values", "table",
        "create", "alter", "drop", "truncate", "comment", "grant", "revoke",
        "begin", "start", "commit", "end", "rollback", "abort", "savepoint", "release",
        "set", "reset", "show", "explain", "analyze", "analyse", "vacuum", "reindex", "cluster", "refresh",
        "copy", "call", "do", "lock", "listen", "notify", "unlisten", "prepare", "execute", "deallocate",
        "discard", "declare", "fetch", "move", "close", "checkpoint", "load", "import", "security",
    };

    private static readonly ConcurrentDictionary<ISqlParser, StatementSplitter> ForParser = new();

    /// <summary>One per provider parser: it holds no state of its own.</summary>
    public static StatementSplitter For(ISqlParser parser) =>
        parser as StatementSplitter ?? ForParser.GetOrAdd(parser, p => new StatementSplitter(p));

    public IReadOnlyList<SqlToken> Tokenize(string sql) => inner.Tokenize(sql);

    public SqlSyntaxError? Validate(string statement) => inner.Validate(statement);

    public IReadOnlyList<SqlRange> Split(string sql)
    {
        var ranges = inner.Split(sql);
        if (ranges.Count == 0 || !sql.Contains('\n')) return ranges;
        var tokens = Tokenize(sql);
        var result = new List<SqlRange>(ranges.Count);
        var from = 0;
        foreach (var r in ranges)
        {
            var (first, last) = TokensIn(tokens, r, ref from);
            var start = r.Start;
            // Each gap between two statement tokens, with any comments between them stepped over.
            for (int a = first, b = NextSignificant(tokens, first, last); b <= last; a = b, b = NextSignificant(tokens, b, last))
            {
                if (Newlines(sql, tokens, a, b) < 2 || !IsStarter(sql, tokens[b])) continue;
                if (Validate(sql[start..tokens[a].End]) is not null) continue; // incomplete above: the blank line is inside the statement
                result.Add(new SqlRange(start, tokens[a].End - start));
                start = tokens[b].Start;
            }
            result.Add(new SqlRange(start, r.End - start));
        }
        return result;
    }

    /// <summary>
    /// Where a <c>;</c> seems to be missing in <paramref name="range"/>: a line (no blank line before it) that starts a new
    /// statement after text that is complete on its own, in a statement that does not parse as a whole. Where the
    /// statement above ends and where the line that begins the next one starts, or null. Only an explanation for a
    /// statement that would fail anyway: valid SQL never has one.
    /// </summary>
    public (int AboveEnd, int NextStart)? MissingSemicolon(string sql, SqlRange range)
    {
        if (!sql.AsSpan(range.Start, range.Length).Contains('\n')) return null;
        if (Validate(sql.Substring(range.Start, range.Length)) is null) return null;
        var tokens = Tokenize(sql);
        var from = 0;
        var (first, last) = TokensIn(tokens, range, ref from);
        for (int a = first, b = NextSignificant(tokens, first, last); b <= last; a = b, b = NextSignificant(tokens, b, last))
        {
            if (Newlines(sql, tokens, a, b) < 1 || !IsStarter(sql, tokens[b])) continue;
            if (Validate(sql[range.Start..tokens[a].End]) is null) return (tokens[a].End, tokens[b].Start);
        }
        return null;
    }

    /// <summary>
    /// The first and last significant tokens inside a statement (ranges start and end on one). Both lists are in text order,
    /// so <paramref name="from"/> carries on where the previous statement stopped: one pass over the tokens in all.
    /// </summary>
    private static (int First, int Last) TokensIn(IReadOnlyList<SqlToken> tokens, SqlRange r, ref int from)
    {
        while (from < tokens.Count && tokens[from].Start < r.Start) from++;
        var first = from;
        while (from < tokens.Count && tokens[from].End <= r.End) from++;
        var last = from - 1;
        while (last > first && tokens[last].Kind == SqlTokenKind.Comment) last--;
        return (first, last);
    }

    /// <summary>The next token after <paramref name="i"/> that is not a comment, or past <paramref name="last"/>.</summary>
    private static int NextSignificant(IReadOnlyList<SqlToken> tokens, int i, int last)
    {
        i++;
        while (i <= last && tokens[i].Kind == SqlTokenKind.Comment) i++;
        return i;
    }

    /// <summary>
    /// The most line breaks in any one stretch of whitespace between tokens <paramref name="a"/> and <paramref name="b"/>.
    /// Two mean an empty line; one means <paramref name="b"/> starts a line. Comments are stepped over whole, so a comment
    /// line is not blank and an empty line inside a block comment does not count.
    /// </summary>
    private static int Newlines(string sql, IReadOnlyList<SqlToken> tokens, int a, int b)
    {
        var most = 0;
        for (var k = a; k < b; k++)
        {
            var n = sql.AsSpan(tokens[k].End, tokens[k + 1].Start - tokens[k].End).Count('\n');
            if (n > most) most = n;
        }
        return most;
    }

    private static bool IsStarter(string sql, SqlToken t) =>
        t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier && Starters.Contains(sql.Substring(t.Start, t.Length));
}
