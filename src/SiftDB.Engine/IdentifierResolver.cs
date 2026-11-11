using SiftDB.Abstractions;

namespace SiftDB.Engine;

public enum IssueCode { CaseMismatch, Ambiguous, StringQuote }

public enum IssueSeverity { Info, Warning, Error }

/// <summary>An edit that is safe to apply without asking: the text is exactly what the database will read it as
/// once quotes are added, with nothing else changed.</summary>
public sealed record Correction(int Start, int Length, string Text, string Message);

/// <summary>A problem the resolver will not fix silently. <see cref="FixText"/> is a suggested replacement, or null
/// when the user has to choose.</summary>
public sealed record Issue(int Start, int Length, IssueCode Code, IssueSeverity Severity, string Message, string? FixText);

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

    public static ResolverResult Resolve(DatabaseSchema schema, ISqlParser parser, IIdentifierPolicy policy, string text)
    {
        var corrections = new List<Correction>();
        var issues = new List<Issue>();
        var tokens = parser.Tokenize(text);
        foreach (var range in parser.Split(text))
        {
            var st = tokens.Where(t => t.Start >= range.Start && t.End <= range.End).ToList();
            ResolveStatement(schema, policy, st, text, corrections, issues);
        }
        return new ResolverResult(corrections, issues);
    }

    private static void ResolveStatement(DatabaseSchema schema, IIdentifierPolicy policy, List<SqlToken> st, string text,
        List<Correction> corrections, List<Issue> issues)
    {
        var scope = ScopeAnalyzer.Analyze(schema, policy, st, text);
        var resolved = scope.Entries.Where(e => e.Rel is not null).ToList();
        if (resolved.Count == 0) return;

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
                if (col is { } c) Check(t, id, c.Name, policy, text, corrections, issues, afterDot: true);
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
                }
                continue;
            }

            // A double-quoted word that is no column: probably meant as a string ('bob', not "bob").
            if (t.Kind == SqlTokenKind.QuotedIdentifier && !mixedScope && prev is { } pv && IsStringPosition(pv, text))
            {
                var fix = "'" + id.Name.Replace("'", "''") + "'";
                issues.Add(new Issue(t.Start, t.Length, IssueCode.StringQuote, IssueSeverity.Warning,
                    $"{id.Raw} is not a column here. Did you mean the string {fix}?", fix));
            }
        }
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
