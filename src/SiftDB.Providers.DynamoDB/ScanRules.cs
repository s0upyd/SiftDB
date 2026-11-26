using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// Whether DynamoDB answers a SELECT with a Query or a full Scan (design section 14, F19 step 4), read from its tokens.
/// AWS's rule: the WHERE must fix the partition key with <c>=</c> or <c>IN</c>
/// (docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.select.html). So: every branch of a top-level
/// OR must fix it, and within a branch one of the conditions ANDed together must be <c>pk = value</c>,
/// <c>value = pk</c> or <c>pk IN […]</c>; parentheses group as written. A condition on any other attribute alone, NOT,
/// a function (<c>begins_with(pk, …)</c>) or no WHERE at all means a scan. Through an index, it is the index's
/// partition key. When in doubt the answer is "scan": a question too many is better than a bill nobody expected.
/// </summary>
internal static class ScanRules
{
    /// <summary>What a SELECT reads, and its WHERE (empty without one).</summary>
    public sealed record Target(string Table, string? Index, IReadOnlyList<SqlToken> Where, string Text);

    /// <summary>The table (and index) a SELECT reads; null for anything else, or a SELECT this can't read.</summary>
    public static Target? SelectTarget(string statement, PartiqlParser parser)
    {
        if (parser.Verb(statement) != "select") return null;
        var ts = parser.Tokenize(statement).Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        var from = Find(ts, statement, "from", 0);
        if (from < 0 || from + 1 >= ts.Count || !IsName(ts[from + 1])) return null;
        var table = Name(statement, ts[from + 1]);
        string? index = null;
        var next = from + 2;
        if (next + 1 < ts.Count && Is(statement, ts[next], ".") && IsName(ts[next + 1]))
        {
            index = Name(statement, ts[next + 1]);
            next += 2;
        }
        var where = Find(ts, statement, "where", next);
        if (where < 0) return new Target(table, index, [], statement);
        var end = Find(ts, statement, "order", where + 1);
        return new Target(table, index, ts.GetRange(where + 1, (end < 0 ? ts.Count : end) - where - 1), statement);
    }

    /// <summary>Whether the WHERE fixes <paramref name="partitionKey"/>, so DynamoDB can answer with a Query.</summary>
    public static bool FixesPartitionKey(Target t, string partitionKey) =>
        t.Where.Count > 0 && Covers(t.Where, t.Text, partitionKey);

    private static bool Covers(IReadOnlyList<SqlToken> ts, string s, string pk)
    {
        ts = Unwrap(ts, s);
        var branches = SplitTop(ts, s, "or");
        if (branches.Count > 1) return branches.All(b => Covers(b, s, pk));
        return SplitTop(ts, s, "and").Any(c => Fixes(c, s, pk));
    }

    /// <summary>One condition: <c>pk = v</c>, <c>v = pk</c>, <c>pk IN […]</c>, or a parenthesised group that covers it.</summary>
    private static bool Fixes(IReadOnlyList<SqlToken> c, string s, string pk)
    {
        var inner = Unwrap(c, s);
        if (inner.Count != c.Count) return Covers(inner, s, pk);
        bool Key(SqlToken t) => IsName(t) && Name(s, t) == pk;
        static bool Value(SqlToken t) => t.Kind is SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Parameter;
        if (c.Count == 3 && Is(s, c[1], "=")) return (Key(c[0]) && Value(c[2])) || (Value(c[0]) && Key(c[2]));
        return c.Count >= 3 && Key(c[0]) && c[1].Kind == SqlTokenKind.Keyword && Word(s, c[1]) == "in";
    }

    /// <summary>Parts between the top-level (outside any brackets) <paramref name="word"/>s.</summary>
    private static List<List<SqlToken>> SplitTop(IReadOnlyList<SqlToken> ts, string s, string word)
    {
        var parts = new List<List<SqlToken>> { new() };
        var depth = 0;
        foreach (var t in ts)
        {
            if (t.Kind == SqlTokenKind.Punctuation && s[t.Start] is '(' or '[' or '{') depth++;
            else if (t.Kind == SqlTokenKind.Punctuation && s[t.Start] is ')' or ']' or '}') depth--;
            if (depth == 0 && t.Kind == SqlTokenKind.Keyword && Word(s, t) == word) { parts.Add([]); continue; }
            parts[^1].Add(t);
        }
        return parts;
    }

    /// <summary>Without the one pair of parentheses round the whole of it, if there is one.</summary>
    private static IReadOnlyList<SqlToken> Unwrap(IReadOnlyList<SqlToken> ts, string s)
    {
        while (ts.Count >= 2 && Is(s, ts[0], "(") && Is(s, ts[^1], ")"))
        {
            var depth = 0;
            for (var i = 0; i < ts.Count; i++)
            {
                if (Is(s, ts[i], "(")) depth++;
                else if (Is(s, ts[i], ")") && --depth == 0 && i < ts.Count - 1) return ts; // "(a) and (b)"
            }
            ts = ts.Skip(1).Take(ts.Count - 2).ToList();
        }
        return ts;
    }

    /// <summary>The first top-level keyword <paramref name="word"/> at or after <paramref name="from"/>, or -1.</summary>
    private static int Find(List<SqlToken> ts, string s, string word, int from)
    {
        var depth = 0;
        for (var i = 0; i < ts.Count; i++)
        {
            var t = ts[i];
            if (t.Kind == SqlTokenKind.Punctuation && s[t.Start] is '(' or '[' or '{') depth++;
            else if (t.Kind == SqlTokenKind.Punctuation && s[t.Start] is ')' or ']' or '}') depth--;
            else if (i >= from && depth == 0 && t.Kind == SqlTokenKind.Keyword && Word(s, t) == word) return i;
        }
        return -1;
    }

    private static bool IsName(SqlToken t) => t.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier;
    private static bool Is(string s, SqlToken t, string text) => s.AsSpan(t.Start, t.Length).SequenceEqual(text);
    private static string Word(string s, SqlToken t) => s[t.Start..t.End].ToLowerInvariant();
    private static string Name(string s, SqlToken t) =>
        t.Kind == SqlTokenKind.QuotedIdentifier ? s[(t.Start + 1)..(t.End - 1)].Replace("\"\"", "\"") : s[t.Start..t.End];
}
