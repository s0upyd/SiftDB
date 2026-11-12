using SiftDB.Abstractions;

namespace SiftDB.Engine;

public enum IssueCode { CaseMismatch, Ambiguous, StringQuote, UnknownColumn, MissingSemicolon, QualifiedSetColumn, NumberForBoolean }

public enum IssueSeverity { Info, Warning, Error }

/// <summary>An edit that is safe to apply without asking: the text is exactly what the database will read it as
/// once quotes are added, with nothing else changed.</summary>
public sealed record Correction(int Start, int Length, string Text, string Message);

/// <summary>A problem the resolver will not fix silently. <see cref="FixText"/> is the suggested replacement, or null when
/// the user has to choose; <see cref="Fixes"/> lists every candidate when there are several (an unknown column).</summary>
public sealed record Issue(int Start, int Length, IssueCode Code, IssueSeverity Severity, string Message, string? FixText,
    IReadOnlyList<string>? Fixes = null)
{
    public IReadOnlyList<string> AllFixes => Fixes ?? (FixText is null ? [] : [FixText]);
}

public sealed record ResolverResult(IReadOnlyList<Correction> Corrections, IReadOnlyList<Issue> Issues)
{
    public static readonly ResolverResult Empty = new([], []);
}

/// <summary>
/// Matches the identifiers a statement uses against the schema and works out how each must be written (design
/// section 3). It only speaks where it is sure: it follows the database's own rules (Postgres folds unquoted names to
/// lower case, so <c>ORDERS</c> already means <c>orders</c>), never touches strings or comments, and says nothing
/// about names it cannot resolve.
/// </summary>
public static class IdentifierResolver
{
    private static readonly HashSet<string> StringContextKeywords = new(StringComparer.OrdinalIgnoreCase) { "like", "ilike" };

    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
        { "select", "from", "where", "by", "having", "set", "on", "using", "returning", "values", "into", "join", "limit", "window" };

    /// <param name="dialect">What else to warn about in this dialect (qualified SET columns, 0/1 for booleans). Permissive if null.</param>
    /// <param name="typeOf">The provider's mapping of a column's data type to a generic type; without it, types are not checked.</param>
    public static ResolverResult Resolve(DatabaseSchema schema, ISqlParser parser, IIdentifierPolicy policy, string text,
        DialectRules? dialect = null, Func<string, GenericType>? typeOf = null)
    {
        var corrections = new List<Correction>();
        var issues = new List<Issue>();
        var tokens = parser.Tokenize(text);
        var rules = new Rules(dialect ?? DialectRules.Permissive, typeOf);
        foreach (var range in parser.Split(text))
        {
            var st = tokens.Where(t => t.Start >= range.Start && t.End <= range.End).ToList();
            ResolveStatement(schema, policy, rules, st, text, corrections, issues);
        }
        return new ResolverResult(corrections, issues);
    }

    private sealed record Rules(DialectRules Dialect, Func<string, GenericType>? TypeOf);

    private static void ResolveStatement(DatabaseSchema schema, IIdentifierPolicy policy, Rules rules, List<SqlToken> st, string text,
        List<Correction> corrections, List<Issue> issues)
    {
        var scope = ScopeAnalyzer.Analyze(schema, policy, st, text);
        var resolved = scope.Entries.Where(e => e.Rel is not null).ToList();
        if (resolved.Count == 0) return;

        if (!rules.Dialect.QualifiedSetColumns) CheckSetTargets(st, text, policy, resolved, issues);

        // 1. The tables themselves.
        foreach (var e in resolved)
        {
            if (e.SchemaTok >= 0 && e.SchemaId is { } sid) Check(st[e.SchemaTok], sid, e.RelSchema!, policy, text, corrections, issues, afterDot: false);
            Check(st[e.NameTok], e.NameId, e.Rel!.Name, policy, text, corrections, issues, afterDot: e.SchemaTok >= 0);
        }

        // Output-column aliases ("... as total") are not columns, whatever else they happen to be called.
        var outputAliases = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < st.Count; i++)
            if (IsKeywordWord(st[i], text, "as") && ScopeAnalyzer.IsName(st[i + 1]) && !scope.Claimed.Contains(i + 1))
                outputAliases.Add(Ident.From(st[i + 1], text).Meaning(policy));

        var mixedScope = resolved.Count != scope.Entries.Count; // a derived table or CTE may own columns we cannot see

        for (var i = 0; i < st.Count; i++)
        {
            var t = st[i];
            if (scope.Claimed.Contains(i) || !CanBeColumnName(t)) continue;

            var prev = i > 0 ? st[i - 1] : (SqlToken?)null;
            var next = i + 1 < st.Count ? st[i + 1] : (SqlToken?)null;
            if (next is { } n0 && IsPunct(n0, text, '(')) continue;                              // a function call
            if (prev is { } p0 && (IsKeywordWord(p0, text, "as") || IsOperator(p0, text, "::"))) continue; // alias or cast type
            var id = Ident.From(t, text);

            // alias.column  (3-part names are left alone)
            if (prev is { } pd && IsPunct(pd, text, '.'))
            {
                if (i < 2 || !ScopeAnalyzer.IsName(st[i - 2]) || (i >= 3 && IsPunct(st[i - 3], text, '.'))) continue;
                var qualifier = Ident.From(st[i - 2], text);
                var owner = resolved.FirstOrDefault(e => (e.AliasId ?? e.NameId).Meaning(policy) == qualifier.Meaning(policy));
                if (owner is null) continue;
                var col = BestColumn(owner.Rel!, id, policy);
                if (col is { } c)
                {
                    Check(t, id, c.Name, policy, text, corrections, issues, afterDot: true);
                    CheckBooleanNumber(st, i, text, owner.Rel!, c.Name, policy, rules, issues);
                }
                else if (CanReportUnknown(st, i, text, mixedScope, checkClause: false)) ReportUnknown(t, id, [owner], policy, issues);
                continue;
            }
            // The left side of "x." is a qualifier, not a column.
            if (next is { } nd && IsPunct(nd, text, '.')) continue;

            if (outputAliases.Contains(id.Meaning(policy))) continue;

            // A bare column: which table(s) in scope could it mean?
            var hits = new List<(ScopeEntry Entry, string Column, int Level)>();
            foreach (var e in resolved)
                if (BestColumn(e.Rel!, id, policy) is { } bc) hits.Add((e, bc.Name, bc.Level));
            if (hits.Count > 0)
            {
                var best = hits.Min(h => h.Level);
                var top = hits.Where(h => h.Level == best).ToList();
                if (top.Count > 1)
                {
                    var owners = string.Join(" or ", top.Select(h => h.Entry.Display + "." + policy.Quote(h.Column)));
                    issues.Add(new Issue(t.Start, t.Length, IssueCode.Ambiguous, IssueSeverity.Error,
                        $"{id.Raw} could mean {owners}. Qualify it with the table or alias you mean.", null));
                }
                else
                {
                    Check(t, id, top[0].Column, policy, text, corrections, issues, afterDot: false);
                    CheckBooleanNumber(st, i, text, top[0].Entry.Rel!, top[0].Column, policy, rules, issues);
                }
                continue;
            }

            // A double-quoted word that is no column: probably meant as a string ('bob', not "bob").
            if (t.Kind == SqlTokenKind.QuotedIdentifier && !mixedScope && prev is { } pv && IsStringPosition(pv, text))
            {
                var fix = "'" + id.Name.Replace("'", "''") + "'";
                issues.Add(new Issue(t.Start, t.Length, IssueCode.StringQuote, IssueSeverity.Warning,
                    $"{id.Raw} is not a column here. Did you mean the string {fix}?", fix));
                continue;
            }

            // A name that is no column of any table in the statement. "select p from Person p" (the whole row) is fine.
            var meaning = id.Meaning(policy);
            var namesATable = scope.Entries.Any(e => (e.AliasId ?? e.NameId).Meaning(policy) == meaning || e.NameId.Meaning(policy) == meaning);
            if (!namesATable && CanReportUnknown(st, i, text, mixedScope, checkClause: true)) ReportUnknown(t, id, resolved, policy, issues);
        }
    }

    // ---- SQL the dialect refuses with an unhelpful message ---------------------------------------------------------

    /// <summary>
    /// <c>UPDATE t a SET a.col = …</c> where the dialect does not allow it (Postgres reads <c>a.col</c> as field <c>col</c>
    /// of a column <c>a</c>, and says column "a" does not exist). Only when the qualifier is the updated table's alias or
    /// name and the table has no column of that name, since <c>SET comp.field = …</c> on a composite column is valid.
    /// </summary>
    private static void CheckSetTargets(List<SqlToken> st, string text, IIdentifierPolicy policy, List<ScopeEntry> resolved,
        List<Issue> issues)
    {
        var update = st.FindIndex(t => IsKeywordWord(t, text, "update"));
        if (update < 0) return;
        var target = resolved.Where(e => e.NameTok > update).MinBy(e => e.NameTok);
        if (target is null) return;
        var set = st.FindIndex(target.NameTok, t => IsKeywordWord(t, text, "set"));
        if (set < 0) return;

        var names = new[] { (target.AliasId ?? target.NameId).Meaning(policy), target.NameId.Meaning(policy) };
        var depth = 0;
        var assignmentStart = true;
        for (var k = set + 1; k < st.Count; k++)
        {
            var t = st[k];
            if (IsPunct(t, text, '(')) { depth++; assignmentStart = false; continue; }
            if (IsPunct(t, text, ')')) { depth--; continue; }
            if (depth > 0) continue;
            if (IsKeywordWord(t, text, "from") || IsKeywordWord(t, text, "where") || IsKeywordWord(t, text, "returning")) return;
            if (IsPunct(t, text, ',')) { assignmentStart = true; continue; }
            if (!assignmentStart) continue;
            assignmentStart = false;

            if (k + 2 >= st.Count || !ScopeAnalyzer.IsName(t) || !IsPunct(st[k + 1], text, '.') || !ScopeAnalyzer.IsName(st[k + 2])) continue;
            var qualifier = Ident.From(t, text);
            if (!names.Contains(qualifier.Meaning(policy)) || BestColumn(target.Rel!, qualifier, policy) is not null) continue;
            var column = Ident.From(st[k + 2], text);
            var fix = BestColumn(target.Rel!, column, policy) is { } c ? policy.Quote(c.Name) : column.Raw;
            issues.Add(new Issue(t.Start, st[k + 2].End - t.Start, IssueCode.QualifiedSetColumn, IssueSeverity.Error,
                $"The column being set cannot be qualified with the table or alias here. Write {fix}, not {qualifier.Raw}.{column.Raw}.", fix));
        }
    }

    /// <summary>
    /// <c>boolcol = 0</c> or <c>set boolcol = 1</c> where the dialect has no integer booleans (Postgres: "operator does
    /// not exist: boolean = integer"). Offers false / true. A cast (<c>1::boolean</c>) or a longer expression is left alone.
    /// </summary>
    private static void CheckBooleanNumber(List<SqlToken> st, int i, string text, RelationInfo rel, string column,
        IIdentifierPolicy policy, Rules rules, List<Issue> issues)
    {
        if (rules.Dialect.NumbersAsBooleans || rules.TypeOf is null || i + 2 >= st.Count) return;
        var op = st[i + 1];
        if (op.Kind != SqlTokenKind.Operator || Text(op, text) is not ("=" or "<>" or "!=")) return;
        var number = st[i + 2];
        if (number.Kind != SqlTokenKind.Number || Text(number, text) is not ("0" or "1")) return;
        if (i + 3 < st.Count && (st[i + 3].Kind == SqlTokenKind.Operator || IsPunct(st[i + 3], text, '.'))) return;
        var info = rel.Columns.FirstOrDefault(c => c.Name == column);
        if (info is null || rules.TypeOf(info.DataType) != GenericType.Boolean) return;
        var word = Text(number, text) == "1" ? "true" : "false";
        issues.Add(new Issue(number.Start, number.Length, IssueCode.NumberForBoolean, IssueSeverity.Warning,
            $"{policy.Quote(column)} is a boolean column: write {word}, not {Text(number, text)}.", word));
    }

    // ---- columns that do not exist -------------------------------------------------------------------------------

    /// <summary>
    /// Whether "this name matches no column" is safe to say. Wrong guesses here would be squiggles on valid SQL, so it is
    /// conservative: every table in the statement must be known (a derived table or CTE could own the column), the word
    /// must be a plain name, and the places where valid SQL uses a bare word that is not a column are skipped.
    /// </summary>
    private static bool CanReportUnknown(List<SqlToken> st, int i, string text, bool mixedScope, bool checkClause)
    {
        if (mixedScope) return false;
        var t = st[i];
        if (t.Kind is not (SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)) return false;
        var prev = i > 0 ? st[i - 1] : (SqlToken?)null;
        var next = i + 1 < st.Count ? st[i + 1] : (SqlToken?)null;

        // A word straight after another expression is an alias given without AS: "select a b", "count(*) c", "case ... end c".
        if (prev is { } before &&
            (before.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.String or SqlTokenKind.Number
             || IsPunct(before, text, ')') || IsKeywordWord(before, text, "end"))) return false;
        // Named arguments: f(width => 3).
        if (next is { } n && n.Kind == SqlTokenKind.Operator && Text(n, text) is "=>" or ":=") return false;
        // extract(epoch from x), substring(x from 2): the first word is not a column.
        if (prev is { } p && IsPunct(p, text, '(') && next is { } nk && IsKeywordWord(nk, text, "from")) return false;
        // ORDER BY / GROUP BY may name an output column that was never given AS ("select a total ... order by total").
        if (checkClause && InByClause(st, i, text)) return false;
        return true;
    }

    /// <summary>True when the closest clause keyword before token <paramref name="i"/> is BY (order by, group by, partition by).</summary>
    private static bool InByClause(List<SqlToken> st, int i, string text)
    {
        var depth = 0;
        for (var k = i - 1; k >= 0; k--)
        {
            var t = st[k];
            if (IsPunct(t, text, ')')) { depth++; continue; }
            if (IsPunct(t, text, '(')) { if (depth > 0) depth--; continue; } // an unmatched "(" means we are inside it: keep looking outward
            if (depth > 0 || t.Kind != SqlTokenKind.Keyword) continue;
            var w = Text(t, text);
            if (ClauseWords.Contains(w)) return w.Equals("by", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void ReportUnknown(SqlToken token, Ident written, IReadOnlyList<ScopeEntry> candidates, IIdentifierPolicy policy, List<Issue> issues)
    {
        var suggestions = Suggest(written.Name, candidates, policy);
        var tables = string.Join(" or ", candidates.Select(e => policy.Quote(e.Rel!.Name)).Distinct());
        var message = suggestions.Count == 0
            ? $"No column {written.Raw} in {tables}."
            : $"No column {written.Raw} in {tables}. Did you mean {string.Join(" or ", suggestions)}?";
        issues.Add(new Issue(token.Start, token.Length, IssueCode.UnknownColumn, IssueSeverity.Warning, message, suggestions.FirstOrDefault(), suggestions));
    }

    /// <summary>
    /// Columns the user probably meant, best first (at most three), as the text to write. In order: a column that ends with
    /// what was typed (<c>id</c> finds <c>PersonId</c>), one that starts with it, a near miss (<c>firstnme</c>), one that
    /// merely contains it. A name that several tables in the statement share is offered qualified.
    /// </summary>
    private static List<string> Suggest(string typed, IReadOnlyList<ScopeEntry> entries, IIdentifierPolicy policy)
    {
        var want = typed.ToLowerInvariant();
        var counts = entries.SelectMany(e => e.Rel!.Columns.Select(c => c.Name))
            .GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var scored = new List<(int Score, int Order, string Text)>();
        var order = 0;
        foreach (var e in entries)
        {
            foreach (var c in e.Rel!.Columns)
            {
                order++;
                var name = c.Name.ToLowerInvariant();
                int score;
                if (want.Length >= 2 && name.EndsWith(want, StringComparison.Ordinal)) score = 1;
                else if (want.Length >= 2 && name.StartsWith(want, StringComparison.Ordinal)) score = 2;
                else
                {
                    var distance = EditDistance(want, name);
                    if (distance <= Math.Max(1, Math.Min(2, want.Length / 3))) score = 3 + distance;
                    else if (want.Length >= 3 && name.Contains(want, StringComparison.Ordinal)) score = 7;
                    else continue;
                }
                var text = entries.Count > 1 && counts[c.Name] > 1 ? e.Display + "." + policy.Quote(c.Name) : policy.Quote(c.Name);
                scored.Add((score, order, text));
            }
        }
        return scored.OrderBy(s => s.Score).ThenBy(s => s.Order).Select(s => s.Text).Distinct().Take(3).ToList();
    }

    private static int EditDistance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return d[b.Length];
    }

    /// <summary>Compares how a name was written with what it must be, and records what (if anything) to do.</summary>
    /// <param name="afterDot">True for the part after a '.', where the grammar accepts any word (<c>o.user</c> is valid).</param>
    private static void Check(SqlToken token, Ident written, string actual, IIdentifierPolicy policy, string text,
        List<Correction> corrections, List<Issue> issues, bool afterDot)
    {
        var level = Level(written, actual, policy);
        if (level == 0) return;

        if (level == 1)
        {
            // It already means the right name. A bare name must also be a word the grammar accepts as a name.
            if (written.Quoted || afterDot || policy.Quote(actual) == actual) return;
            corrections.Add(new Correction(token.Start, token.Length, policy.Quote(actual),
                $"{written.Raw} is a reserved word here; it must be written {policy.Quote(actual)}"));
            return;
        }
        if (level == 2)
        {
            // The spelling is right but unquoted it would be read as something else (Person -> person).
            corrections.Add(new Correction(token.Start, token.Length, policy.Quote(actual),
                $"{written.Raw} must be quoted: unquoted it means {policy.Fold(written.Name)}, not {actual}"));
            return;
        }
        // Only the case differs, so we do not know which was intended.
        issues.Add(new Issue(token.Start, token.Length, IssueCode.CaseMismatch, IssueSeverity.Warning,
            $"{written.Raw} differs in case from {policy.Quote(actual)}", policy.Quote(actual)));
    }

    /// <summary>1 = it already means <paramref name="actual"/>; 2 = same spelling but would fold differently;
    /// 3 = differs only in case; 0 = no relation.</summary>
    private static int Level(Ident id, string actual, IIdentifierPolicy policy)
    {
        if (id.Meaning(policy) == actual) return 1;
        if (id.Name == actual) return 2;
        return string.Equals(id.Name, actual, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
    }

    private static (string Name, int Level)? BestColumn(RelationInfo rel, Ident id, IIdentifierPolicy policy)
    {
        (string, int)? best = null;
        foreach (var c in rel.Columns)
        {
            var l = Level(id, c.Name, policy);
            if (l != 0 && (best is null || l < best.Value.Item2)) best = (c.Name, l);
        }
        return best;
    }

    private static bool CanBeColumnName(SqlToken t) =>
        t.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier
        || (t.Kind == SqlTokenKind.Keyword && t.Keyword is KeywordClass.Unreserved or KeywordClass.ColumnName);

    private static bool IsStringPosition(SqlToken prev, string text) =>
        (prev.Kind == SqlTokenKind.Operator && Text(prev, text) is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=" or "||")
        || (prev.Kind == SqlTokenKind.Keyword && StringContextKeywords.Contains(Text(prev, text)));

    private static string Text(SqlToken t, string text) => ScopeAnalyzer.Text(t, text);
    private static bool IsPunct(SqlToken t, string text, char c) => ScopeAnalyzer.IsPunct(t, text, c);
    private static bool IsKeywordWord(SqlToken t, string text, string w) => ScopeAnalyzer.IsKeywordWord(t, text, w);
    private static bool IsOperator(SqlToken t, string text, string op) => t.Kind == SqlTokenKind.Operator && Text(t, text) == op;
}
