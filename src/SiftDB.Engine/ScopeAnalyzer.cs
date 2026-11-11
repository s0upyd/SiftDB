using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>An identifier as written: <see cref="Raw"/> is the token text, <see cref="Name"/> the name inside any quotes.</summary>
internal readonly record struct Ident(string Raw, string Name, bool Quoted)
{
    public static Ident From(SqlToken t, string text)
    {
        var raw = text.Substring(t.Start, t.Length);
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') return new Ident(raw, raw[1..^1].Replace("\"\"", "\""), true);
        if (raw.Length >= 1 && raw[0] == '"') return new Ident(raw, raw[1..].Replace("\"\"", "\""), true); // unterminated
        return new Ident(raw, raw, false);
    }

    /// <summary>The name the database will look up for this identifier.</summary>
    public string Meaning(IIdentifierPolicy policy) => Quoted ? Name : policy.Fold(Name);
}

/// <summary>One table (or derived table) a statement reads from, with the tokens that spell it.</summary>
internal sealed record ScopeEntry(
    Ident? SchemaId, int SchemaTok, Ident NameId, int NameTok, Ident? AliasId, int AliasTok, string? RelSchema, RelationInfo? Rel)
{
    /// <summary>How later parts of the statement refer to it: its alias if it has one, else its name, as written.</summary>
    public string Display => (AliasId ?? NameId).Raw;
}

internal sealed record ScopeResult(List<ScopeEntry> Entries, HashSet<int> Claimed);

/// <summary>
/// Finds the tables a statement mentions (after FROM, JOIN, INTO, UPDATE), with aliases, resolved against the schema.
/// Works on tokens, so it copes with half-typed SQL. Shared by completion and the identifier resolver.
/// Token indexes refer to the statement's token list; -1 means "no such token".
/// </summary>
internal static class ScopeAnalyzer
{
    public static ScopeResult Analyze(DatabaseSchema schema, IIdentifierPolicy policy, List<SqlToken> st, string text)
    {
        var entries = new List<ScopeEntry>();
        var claimed = new HashSet<int>();
        for (var i = 0; i < st.Count; i++)
        {
            if (st[i].Kind != SqlTokenKind.Keyword) continue;
            var w = Text(st[i], text).ToLowerInvariant();
            if (w is not ("from" or "join" or "update" or "into")) continue;

            var next = ParseRef(schema, policy, st, i + 1, text, entries, claimed);
            if (w == "from")
                while (next < st.Count && IsPunct(st[next], text, ','))
                    next = ParseRef(schema, policy, st, next + 1, text, entries, claimed);
            i = Math.Max(i, next - 1);
        }
        return new ScopeResult(entries, claimed);
    }

    /// <summary>Reads <c>[only] [schema.]name [[as] alias]</c> (or a parenthesised derived table); returns the next index.</summary>
    private static int ParseRef(DatabaseSchema schema, IIdentifierPolicy policy, List<SqlToken> st, int i, string text,
        List<ScopeEntry> entries, HashSet<int> claimed)
    {
        if (i < st.Count && IsKeywordWord(st[i], text, "only")) i++;
        if (i >= st.Count) return i;

        if (IsPunct(st[i], text, '('))
        {
            i = SkipParens(st, i, text);
            var derived = ReadAlias(st, ref i, text, out var aliasTok);
            if (derived is { } d)
            {
                claimed.Add(aliasTok);
                entries.Add(new ScopeEntry(null, -1, d, -1, d, aliasTok, null, null)); // columns unknown
            }
            return i;
        }
        if (!IsName(st[i])) return i;

        var firstTok = i;
        var first = Ident.From(st[i], text);
        i++;
        Ident? schemaId = null;
        var schemaTok = -1;
        var nameId = first;
        var nameTok = firstTok;
        if (i + 1 < st.Count && IsPunct(st[i], text, '.') && IsName(st[i + 1]))
        {
            schemaId = first;
            schemaTok = firstTok;
            nameTok = i + 1;
            nameId = Ident.From(st[i + 1], text);
            i += 2;
        }
        var alias = ReadAlias(st, ref i, text, out var aliasTokIdx);

        claimed.Add(nameTok);
        if (schemaTok >= 0) claimed.Add(schemaTok);
        if (alias is not null) claimed.Add(aliasTokIdx);

        var resolved = Resolve(schema, policy, schemaId, nameId);
        entries.Add(new ScopeEntry(schemaId, schemaTok, nameId, nameTok, alias, alias is null ? -1 : aliasTokIdx, resolved?.Schema, resolved?.Rel));
        return i;
    }

    private static Ident? ReadAlias(List<SqlToken> st, ref int i, string text, out int tokenIndex)
    {
        tokenIndex = -1;
        if (i + 1 < st.Count && IsKeywordWord(st[i], text, "as") && IsName(st[i + 1]))
        {
            tokenIndex = i + 1;
            var a = Ident.From(st[i + 1], text);
            i += 2;
            return a;
        }
        if (i < st.Count && st[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)
        {
            tokenIndex = i;
            var a = Ident.From(st[i], text);
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

    /// <summary>
    /// Finds the relation a written name refers to. In order of confidence: what the database would really look up
    /// (so <c>PERSON</c> finds <c>person</c>), then the exact spelling, then any case-insensitive match (so
    /// <c>person</c> still finds <c>"Person"</c>: the resolver will offer the fix). <c>public</c> is searched first.
    /// </summary>
    public static (string Schema, RelationInfo Rel)? Resolve(DatabaseSchema schema, IIdentifierPolicy policy, Ident? schemaId, Ident nameId)
    {
        IEnumerable<SchemaInfo> candidates;
        if (schemaId is { } sid)
        {
            var wanted = sid.Meaning(policy);
            candidates = schema.Schemas.Where(s => s.Name == wanted || s.Name == sid.Name)
                .Concat(schema.Schemas.Where(s => string.Equals(s.Name, sid.Name, StringComparison.OrdinalIgnoreCase)))
                .Distinct();
        }
        else
        {
            candidates = schema.Schemas.OrderBy(s => string.Equals(s.Name, "public", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        var meaning = nameId.Meaning(policy);
        foreach (var s in candidates)
        {
            var rel = s.Relations.FirstOrDefault(r => r.Name == meaning)
                      ?? s.Relations.FirstOrDefault(r => r.Name == nameId.Name)
                      ?? s.Relations.FirstOrDefault(r => string.Equals(r.Name, nameId.Name, StringComparison.OrdinalIgnoreCase));
            if (rel is not null) return (s.Name, rel);
        }
        return null;
    }

    // ---- token helpers shared with the engines that use this ------------------------------------------------

    public static string Text(SqlToken t, string text) => text.Substring(t.Start, t.Length);
    public static bool IsPunct(SqlToken t, string text, char c) => t.Kind == SqlTokenKind.Punctuation && text[t.Start] == c;
    public static bool IsKeywordWord(SqlToken t, string text, string word) =>
        t.Kind == SqlTokenKind.Keyword && Text(t, text).Equals(word, StringComparison.OrdinalIgnoreCase);
    public static bool IsName(SqlToken t) => t.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Keyword;
}
