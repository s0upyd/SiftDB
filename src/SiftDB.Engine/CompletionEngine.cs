using SiftDB.Abstractions;

namespace SiftDB.Engine;

public enum CompletionKind { Schema, Table, View, Column, Alias }

/// <param name="InsertText">What goes into the editor, already quoted where the dialect needs it.</param>
/// <param name="FilterText">What the typed prefix is matched against (so typing <c>per</c> finds <c>"Person"</c>).</param>
public sealed record CompletionItem(
    string Label, string InsertText, string FilterText, CompletionKind Kind, string? Detail, string SortText);

/// <summary>Items plus the span of text they replace (UTF-16 offsets into the text that was analysed).</summary>
public sealed record CompletionResult(int Start, int End, IReadOnlyList<CompletionItem> Items);

/// <summary>
/// Schema-aware suggestions for half-typed SQL (design section 5). The dialect's parser has no error recovery, so
/// this works on tokens: it finds the statement under the cursor, the tables it mentions (with aliases), and what the
/// words before the cursor allow. Tables are offered after FROM/JOIN/INTO/UPDATE, columns after SELECT/WHERE/ON/...
/// and after <c>alias.</c>. Anything it is not sure about yields no suggestions rather than wrong ones.
/// </summary>
public static class CompletionEngine
{
    private const int MaxItems = 300;

    private static readonly HashSet<string> RelationWords = new(StringComparer.OrdinalIgnoreCase)
        { "from", "join", "into", "update", "truncate" };

    private static readonly HashSet<string> ColumnWords = new(StringComparer.OrdinalIgnoreCase)
        { "select", "where", "and", "or", "on", "by", "having", "set", "distinct", "when", "then", "else", "case", "not", "returning" };

    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
        { "select", "from", "where", "by", "having", "set", "on", "using", "returning", "values", "into", "join", "limit", "window" };

    private readonly record struct Scope(string? SchemaName, string Name, string? Alias, string? RelSchema, RelationInfo? Rel)
    {
        public string Display => Alias ?? Name;
    }

    private enum Ctx { None, Relation, Column }

    public static CompletionResult Complete(
        DatabaseSchema schema, ISqlParser parser, IIdentifierPolicy policy, string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var none = new CompletionResult(offset, offset, []);

        var statement = StatementAt(parser.Split(text), text, offset);
        if (statement is null) return none;
        var st = parser.Tokenize(text).Where(t => t.Start >= statement.Value.Start && t.End <= statement.Value.End).ToList();

        // The token the cursor is in or touching the end of.
        SqlToken? cur = null;
        foreach (var t in st) if (t.Start < offset && offset <= t.End) cur = t;

        var word = false;
        if (cur is { } c)
        {
            word = c.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Keyword
                || (c.Kind == SqlTokenKind.Invalid && text[c.Start] == '"');
            // Inside a string, comment, number or other unterminated text: nothing sensible to offer.
            if (!word && c.Kind is not (SqlTokenKind.Operator or SqlTokenKind.Punctuation)) return none;
        }

        var wordStart = word ? cur!.Value.Start : offset;
        // An unterminated quote runs to the end of the text; never swallow what follows the cursor.
        var wordEnd = !word ? offset : cur!.Value.Kind == SqlTokenKind.Invalid ? offset : cur.Value.End;
        var typed = word ? text[wordStart..offset] : "";
        var quotedTyped = typed.StartsWith('"');
        var prefix = quotedTyped ? typed[1..] : typed;

        var prior = st.Where(t => t.End <= wordStart).ToList();
        var scope = FindScope(schema, st, text);
        var items = new List<CompletionItem>();

        if (prior.Count > 0 && IsPunct(prior[^1], text, '.'))
        {
            QualifiedItems(schema, policy, scope, prior, text, prefix, quotedTyped, items);
        }
        else
        {
            switch (Classify(prior, text))
            {
                case Ctx.Relation: RelationItems(schema, policy, prefix, quotedTyped, items); break;
                case Ctx.Column: ColumnItems(policy, scope, prefix, quotedTyped, items); break;
            }
        }

        var ordered = items.OrderBy(i => i.SortText, StringComparer.Ordinal).Take(MaxItems).ToList();
        return new CompletionResult(wordStart, wordEnd, ordered);
    }

    // ---- which statement ------------------------------------------------------------------------------------

    /// <summary>The statement the cursor is in, or the one it trails after whitespace only (<c>from |</c>).</summary>
    private static SqlRange? StatementAt(IReadOnlyList<SqlRange> statements, string text, int offset)
    {
        foreach (var s in statements)
            if (offset >= s.Start && offset <= s.End) return s;
        SqlRange? before = null;
        foreach (var s in statements)
            if (s.End <= offset) before = s; else break;
        if (before is { } b && string.IsNullOrWhiteSpace(text[b.End..offset])) return b;
        return null;
    }

    // ---- what is allowed here -------------------------------------------------------------------------------

    private static Ctx Classify(List<SqlToken> prior, string text)
    {
        if (prior.Count == 0) return Ctx.None;
        var prev = prior[^1];
        var prevWord = prev.Kind == SqlTokenKind.Keyword ? Text(prev, text).ToLowerInvariant() : null;

        if (prevWord is not null)
        {
            if (RelationWords.Contains(prevWord))
            {
                // "for update", "do update set": not a table position.
                if (prevWord == "update" && prior.Count >= 2 && IsAnyKeyword(prior[^2], text, "for", "do")) return Ctx.None;
                return Ctx.Relation;
            }
            if (prevWord == "table" && prior.Count >= 2 && IsAnyKeyword(prior[^2], text, "alter", "drop", "truncate", "lock"))
                return Ctx.Relation;
            return ColumnWords.Contains(prevWord) ? Ctx.Column : Ctx.None;
        }

        if (prev.Kind == SqlTokenKind.Operator)
        {
            // "select *" and "a.*" end a select item; a bare * after another operator is multiplication.
            if (Text(prev, text) == "*" && (prior.Count == 1 || IsKeywordWord(prior[^2], text, "select")
                                            || IsPunct(prior[^2], text, ',') || IsPunct(prior[^2], text, '.')))
                return Ctx.None;
            return Ctx.Column;
        }

        if (IsPunct(prev, text, ',') || IsPunct(prev, text, '('))
        {
            var clause = NearestClause(prior, text);
            return clause switch
            {
                "from" => IsPunct(prev, text, ',') ? Ctx.Relation : Ctx.None, // "from (" starts a derived table
                "select" or "where" or "set" or "by" or "having" or "on" or "returning" or "into" => Ctx.Column,
                _ => Ctx.None,
            };
        }
        return Ctx.None;
    }

    /// <summary>The closest clause keyword before the end of <paramref name="prior"/>, stepping over balanced parentheses.</summary>
    private static string? NearestClause(List<SqlToken> prior, string text)
    {
        var depth = 0;
        for (var i = prior.Count - 2; i >= 0; i--)
        {
            var t = prior[i];
            if (IsPunct(t, text, ')')) { depth++; continue; }
            if (IsPunct(t, text, '(')) { if (depth > 0) depth--; continue; }
            if (depth > 0 || t.Kind != SqlTokenKind.Keyword) continue;
            var w = Text(t, text).ToLowerInvariant();
            if (ClauseWords.Contains(w)) return w;
        }
        return null;
    }

    // ---- tables in scope ------------------------------------------------------------------------------------

    private static List<Scope> FindScope(DatabaseSchema schema, List<SqlToken> st, string text)
    {
        var scope = new List<Scope>();
        for (var i = 0; i < st.Count; i++)
        {
            if (st[i].Kind != SqlTokenKind.Keyword) continue;
            var w = Text(st[i], text).ToLowerInvariant();
            if (w is not ("from" or "join" or "update" or "into")) continue;

            var next = ParseRef(schema, st, i + 1, text, scope);
            if (w == "from")
                while (next < st.Count && IsPunct(st[next], text, ','))
                    next = ParseRef(schema, st, next + 1, text, scope);
            i = Math.Max(i, next - 1);
        }
        return scope;
    }

    /// <summary>Reads <c>[only] [schema.]name [[as] alias]</c> (or a parenthesised derived table) and returns the next index.</summary>
    private static int ParseRef(DatabaseSchema schema, List<SqlToken> st, int i, string text, List<Scope> scope)
    {
        if (i < st.Count && IsKeywordWord(st[i], text, "only")) i++;
        if (i >= st.Count) return i;

        if (IsPunct(st[i], text, '('))
        {
            i = SkipParens(st, i, text);
            var derived = ReadAlias(st, ref i, text);
            if (derived is not null) scope.Add(new Scope(null, derived, derived, null, null)); // columns unknown
            return i;
        }
        if (!IsName(st[i])) return i;

        var first = NameOf(st[i], text);
        i++;
        string? schemaName = null;
        var name = first;
        if (i + 1 < st.Count && IsPunct(st[i], text, '.') && IsName(st[i + 1]))
        {
            schemaName = first;
            name = NameOf(st[i + 1], text);
            i += 2;
        }
        var alias = ReadAlias(st, ref i, text);
        var resolved = Resolve(schema, schemaName, name);
        scope.Add(new Scope(schemaName, name, alias, resolved?.Schema, resolved?.Rel));
        return i;
    }

    private static string? ReadAlias(List<SqlToken> st, ref int i, string text)
    {
        if (i + 1 < st.Count && IsKeywordWord(st[i], text, "as") && IsName(st[i + 1]))
        {
            var a = NameOf(st[i + 1], text);
            i += 2;
            return a;
        }
        if (i < st.Count && st[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)
        {
            var a = NameOf(st[i], text);
            i++;
            return a;
        }
        return null;
    }

    private static int SkipParens(List<SqlToken> st, int i, string text)
    {
        var depth = 0;
        for (; i < st.Count; i++)
        {
            if (IsPunct(st[i], text, '(')) depth++;
            else if (IsPunct(st[i], text, ')') && --depth == 0) return i + 1;
        }
        return i;
    }

    /// <summary>Case-insensitive on purpose: the identifier resolver (design section 3) matches ignoring case, so
    /// <c>person</c> should still find <c>"Person"</c>. Exact case wins when both exist; <c>public</c> is searched first.</summary>
    private static (string Schema, RelationInfo Rel)? Resolve(DatabaseSchema schema, string? schemaName, string name)
    {
        IEnumerable<SchemaInfo> candidates = schemaName is not null
            ? schema.Schemas.Where(s => Eq(s.Name, schemaName))
            : schema.Schemas.OrderBy(s => Eq(s.Name, "public") ? 0 : 1);
        foreach (var s in candidates)
        {
            var rel = s.Relations.FirstOrDefault(r => r.Name == name) ?? s.Relations.FirstOrDefault(r => Eq(r.Name, name));
            if (rel is not null) return (s.Name, rel);
        }
        return null;
    }

    // ---- building the items ---------------------------------------------------------------------------------

    private static void RelationItems(DatabaseSchema schema, IIdentifierPolicy policy, string prefix, bool quoted, List<CompletionItem> items)
    {
        foreach (var s in schema.Schemas)
        {
            var isPublic = Eq(s.Name, "public");
            var rank = isPublic ? "0" : "1";
            foreach (var r in s.Relations)
            {
                var group = MatchGroup(r.Name, prefix);
                if (group < 0) continue;
                var insert = isPublic ? policy.Quote(r.Name) : policy.Quote(s.Name) + "." + policy.Quote(r.Name);
                items.Add(new CompletionItem(r.Name, insert, Filter(r.Name, quoted), KindOf(r.Kind),
                    DescribeKind(r.Kind) + (isPublic ? "" : " · " + s.Name), $"{group}{rank}{r.Name.ToLowerInvariant()}"));
            }
            if (!isPublic && MatchGroup(s.Name, prefix) >= 0)
                items.Add(new CompletionItem(s.Name, policy.Quote(s.Name) + ".", Filter(s.Name, quoted), CompletionKind.Schema,
                    "schema", $"{MatchGroup(s.Name, prefix)}2{s.Name.ToLowerInvariant()}"));
        }
    }

    private static void QualifiedItems(DatabaseSchema schema, IIdentifierPolicy policy, List<Scope> scope, List<SqlToken> prior,
        string text, string prefix, bool quoted, List<CompletionItem> items)
    {
        // prior ends with '.', preceded by name or schema.name
        var n = prior.Count;
        if (n < 2 || !IsName(prior[n - 2])) return;
        var last = NameOf(prior[n - 2], text);
        string? before = n >= 4 && IsPunct(prior[n - 3], text, '.') && IsName(prior[n - 4]) ? NameOf(prior[n - 4], text) : null;

        // schema.table.  -> columns
        if (before is not null)
        {
            if (Resolve(schema, before, last) is { } r) AddColumns(policy, r.Rel, r.Schema + "." + r.Rel.Name, qualifier: null, prefix, quoted, items);
            return;
        }

        // alias.  or  table.  (an aliased table is only reachable through its alias)
        var hit = scope.FirstOrDefault(s => s.Rel is not null && (s.Alias is not null ? Eq(s.Alias, last) : Eq(s.Name, last)));
        if (hit.Rel is not null)
        {
            AddColumns(policy, hit.Rel, hit.RelSchema + "." + hit.Rel.Name, qualifier: null, prefix, quoted, items);
            return;
        }

        // schema.  -> its tables, named without the schema (it is already typed)
        var sch = schema.Schemas.FirstOrDefault(s => Eq(s.Name, last));
        if (sch is null) return;
        foreach (var r in sch.Relations)
        {
            var group = MatchGroup(r.Name, prefix);
            if (group < 0) continue;
            items.Add(new CompletionItem(r.Name, policy.Quote(r.Name), Filter(r.Name, quoted), KindOf(r.Kind),
                DescribeKind(r.Kind), $"{group}0{r.Name.ToLowerInvariant()}"));
        }
    }

    private static void ColumnItems(IIdentifierPolicy policy, List<Scope> scope, string prefix, bool quoted, List<CompletionItem> items)
    {
        var resolved = scope.Where(s => s.Rel is not null).ToList();
        if (resolved.Count == 0) return;

        // A column name that several tables in scope share must be qualified to mean anything.
        var counts = resolved.SelectMany(s => s.Rel!.Columns.Select(c => c.Name)).GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var s in resolved)
        {
            var q = resolved.Count > 1 ? policy.Quote(s.Display) : null;
            AddColumns(policy, s.Rel!, s.Display, q, prefix, quoted, items, name => counts[name] > 1);

            if (MatchGroup(s.Display, prefix) >= 0)
                items.Add(new CompletionItem(s.Display, policy.Quote(s.Display), Filter(s.Display, quoted), CompletionKind.Alias,
                    "alias for " + s.Name, $"{MatchGroup(s.Display, prefix)}9{s.Display.ToLowerInvariant()}"));
        }
    }

    /// <param name="qualifier">Quoted alias to put in front when <paramref name="mustQualify"/> says the name is ambiguous.</param>
    private static void AddColumns(IIdentifierPolicy policy, RelationInfo rel, string owner, string? qualifier, string prefix, bool quoted,
        List<CompletionItem> items, Func<string, bool>? mustQualify = null)
    {
        var pk = new HashSet<string>(rel.PrimaryKey, StringComparer.Ordinal);
        for (var i = 0; i < rel.Columns.Count; i++)
        {
            var col = rel.Columns[i];
            var group = MatchGroup(col.Name, prefix);
            if (group < 0) continue;
            var insert = qualifier is not null && mustQualify?.Invoke(col.Name) == true
                ? qualifier + "." + policy.Quote(col.Name)
                : policy.Quote(col.Name);
            var detail = col.DataType + (col.IsNullable ? "" : " not null") + (pk.Contains(col.Name) ? " · PK" : "") + " · " + owner;
            items.Add(new CompletionItem(col.Name, insert, Filter(col.Name, quoted), CompletionKind.Column, detail, $"{group}1{i:D4}"));
        }
    }

    // ---- small helpers --------------------------------------------------------------------------------------

    /// <summary>0 = starts with the prefix, 1 = contains it, -1 = no match (case-insensitive).</summary>
    private static int MatchGroup(string name, string prefix)
    {
        if (prefix.Length == 0) return 0;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        return name.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    /// <summary>Monaco matches the word in the replaced range against this; if it starts with a quote, so must this.</summary>
    private static string Filter(string name, bool typedQuote) => typedQuote ? "\"" + name + "\"" : name;

    private static CompletionKind KindOf(RelationKind k) =>
        k is RelationKind.View or RelationKind.MaterializedView ? CompletionKind.View : CompletionKind.Table;

    private static string DescribeKind(RelationKind k) => k switch
    {
        RelationKind.PartitionedTable => "partitioned table",
        RelationKind.MaterializedView => "materialized view",
        RelationKind.ForeignTable => "foreign table",
        RelationKind.View => "view",
        _ => "table",
    };

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Text(SqlToken t, string text) => text.Substring(t.Start, t.Length);
    private static bool IsPunct(SqlToken t, string text, char c) => t.Kind == SqlTokenKind.Punctuation && text[t.Start] == c;
    private static bool IsKeywordWord(SqlToken t, string text, string word) =>
        t.Kind == SqlTokenKind.Keyword && Text(t, text).Equals(word, StringComparison.OrdinalIgnoreCase);
    private static bool IsAnyKeyword(SqlToken t, string text, params string[] words) => words.Any(w => IsKeywordWord(t, text, w));
    private static bool IsName(SqlToken t) => t.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Keyword;

    /// <summary>The identifier's name: quotes removed and doubled quotes collapsed.</summary>
    private static string NameOf(SqlToken t, string text)
    {
        var s = Text(t, text);
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1].Replace("\"\"", "\"");
        return s;
    }
}
