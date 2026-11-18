using SiftDB.Abstractions;
using static SiftDB.Engine.ScopeAnalyzer;

namespace SiftDB.Engine;

/// <summary>A quick fix the editor offers with no problem behind it (no squiggle): an edit, and what it is called.</summary>
public sealed record CodeAction(int Start, int Length, string Text, string Title);

/// <summary>
/// Expand <c>*</c> (design section 14, F10): a quick fix on a <c>*</c> in a select list replaces it with the columns it
/// stands for, in table order, quoted as the dialect needs. A bare <c>*</c> over several tables gives every table's
/// columns, each qualified with its alias (or table name); <c>o.*</c> gives that table's, qualified the same way.
///
/// It is offered only where the result cannot differ from what <c>*</c> returns, so only in a plain SELECT whose tables
/// are all in the schema cache. Not with a CTE, a derived table, a function in FROM, any subquery (the scope is read
/// flat, so a subquery's tables would wrongly join the outer list), a UNION/INTERSECT/EXCEPT, a <c>USING</c> or
/// <c>NATURAL</c> join (there <c>*</c> shows the shared column once), or a column alias list (<c>t AS x(a, b)</c>
/// renames the columns). <c>count(*)</c> and other arguments are never offered.
/// </summary>
public static class StarExpander
{
    public static IReadOnlyList<CodeAction> Find(DatabaseSchema schema, ISqlParser parser, IIdentifierPolicy policy, string text,
        FormatOptions layout)
    {
        var actions = new List<CodeAction>();
        var tokens = parser.Tokenize(text);
        foreach (var range in parser.Split(text))
        {
            var st = tokens.Where(t => t.Start >= range.Start && t.End <= range.End).ToList();
            if (st.Any(t => t.Kind == SqlTokenKind.Operator && text[t.Start] == '*' && t.Length == 1)) Statement(schema, policy, st, text, layout, actions);
        }
        return actions;
    }

    private static void Statement(DatabaseSchema schema, IIdentifierPolicy policy, List<SqlToken> st, string text, FormatOptions layout,
        List<CodeAction> actions)
    {
        if (st.Count == 0 || !IsKeywordWord(st[0], text, "select")) return;
        // Anything that makes the columns of * more than "every column of every table in FROM", or that the flat scope
        // cannot place, means no offer.
        for (var i = 0; i < st.Count; i++)
        {
            var t = st[i];
            if (t.Kind == SqlTokenKind.Keyword && Word(t, text) is "with" or "union" or "intersect" or "except" or "using" or "natural") return;
            if (IsPunct(t, text, '(') && i + 1 < st.Count && st[i + 1].Kind == SqlTokenKind.Keyword
                && Word(st[i + 1], text) is "select" or "with" or "values") return;
        }
        var scope = Analyze(schema, policy, st, text);
        if (scope.Entries.Count == 0 || scope.Entries.Any(e => e.Rel is null)) return;
        foreach (var e in scope.Entries)
        {
            var last = Math.Max(e.AliasTok, e.NameTok);
            if (last + 1 < st.Count && IsPunct(st[last + 1], text, '(')) return; // t AS x(a, b), or a function in FROM
        }

        var from = st.FindIndex(t => IsKeywordWord(t, text, "from"));
        if (from < 0) return;
        var depth = 0;
        for (var i = 1; i < from; i++)
        {
            var t = st[i];
            if (IsPunct(t, text, '(')) { depth++; continue; }
            if (IsPunct(t, text, ')')) { depth--; continue; }
            if (depth != 0 || !(t.Kind == SqlTokenKind.Operator && t.Length == 1 && text[t.Start] == '*')) continue;
            // A whole item: what follows is the end of the list or the next item, never "* 2".
            if (!(IsPunct(st[i + 1], text, ',') || i + 1 == from)) continue;

            var prev = st[i - 1];
            if (IsPunct(prev, text, '.'))
            {
                // qualifier.* ; a three-part schema.table.* is left alone
                if (i < 3 || !IsName(st[i - 2]) || IsPunct(st[i - 3], text, '.') || !StartsItem(st[i - 3], text)) continue;
                var q = Ident.From(st[i - 2], text).Meaning(policy);
                var owner = scope.Entries.FirstOrDefault(e => (e.AliasId ?? e.NameId).Meaning(policy) == q);
                if (owner is null) continue;
                Add(actions, st, i - 2, i, text, [owner], policy, layout, qualify: true);
            }
            else if (StartsItem(prev, text))
            {
                Add(actions, st, i, i, text, scope.Entries, policy, layout, qualify: scope.Entries.Count > 1);
            }
        }
    }

    /// <summary>What may come just before a select-list item: SELECT, DISTINCT, ALL, or a comma.</summary>
    private static bool StartsItem(SqlToken t, string text) =>
        IsPunct(t, text, ',') || (t.Kind == SqlTokenKind.Keyword && Word(t, text) is "select" or "distinct" or "all");

    private static void Add(List<CodeAction> actions, List<SqlToken> st, int first, int star, string text, IReadOnlyList<ScopeEntry> owners,
        IIdentifierPolicy policy, FormatOptions layout, bool qualify)
    {
        var columns = owners.SelectMany(e => e.Rel!.Columns.Select(c => (qualify ? Qualifier(e, policy) + "." : "") + policy.Quote(c.Name))).ToList();
        if (columns.Count == 0) return;

        // On one line if it fits the formatter's line width, else one column per line, indented one step past the line.
        var start = st[first].Start;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var oneLine = string.Join(", ", columns);
        string replacement;
        int from;
        if (start - lineStart + oneLine.Length <= layout.LineWidth)
        {
            replacement = oneLine;
            from = start;
        }
        else
        {
            var indent = "\n" + new string(' ', LeadingSpaces(text, lineStart) + Math.Clamp(layout.Indent, 1, 8));
            replacement = layout.Commas == CommaPlacement.Start
                ? indent + columns[0] + string.Concat(columns.Skip(1).Select(c => indent + ", " + c))
                : indent + string.Join("," + indent, columns);
            from = st[first - 1].End; // the space after SELECT (or the comma) goes: the list starts on a line of its own
        }
        var what = owners.Count == 1 ? owners[0].Display : $"{owners.Count} tables";
        actions.Add(new CodeAction(from, st[star].End - from, replacement,
            $"Expand {(first == star ? "*" : owners[0].Display + ".*")} to the {columns.Count} column{(columns.Count == 1 ? "" : "s")} of {what}"));
    }

    /// <summary>How a column is qualified: by the alias as written, or by the table's name as the dialect needs it written.</summary>
    private static string Qualifier(ScopeEntry e, IIdentifierPolicy policy) => e.AliasId is { } a ? a.Raw : policy.Quote(e.Rel!.Name);

    private static int LeadingSpaces(string text, int lineStart)
    {
        var n = 0;
        while (lineStart + n < text.Length && text[lineStart + n] is ' ' or '\t') n++;
        return n;
    }

    private static string Word(SqlToken t, string text) => text.Substring(t.Start, t.Length).ToLowerInvariant();
}
