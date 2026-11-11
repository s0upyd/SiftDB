using System.Collections.Concurrent;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Postgres folds unquoted identifiers to lower case, so a name is safe bare only if it is already lower case, made of
/// plain characters, and not a word the grammar reserves. Which words those are comes from libpg_query itself (through
/// the tokenizer's keyword classes), not from a list we maintain.
/// </summary>
public sealed class PostgresIdentifierPolicy(ISqlParser parser) : IIdentifierPolicy
{
    private readonly ConcurrentDictionary<string, bool> _needsQuotes = new();

    public string Quote(string identifier) =>
        NeedsQuotes(identifier) ? "\"" + identifier.Replace("\"", "\"\"") + "\"" : identifier;

    /// <summary>ASCII-only, as Postgres does for a UTF-8 database: non-ASCII letters are left alone.</summary>
    public string Fold(string unquotedIdentifier)
    {
        var any = false;
        foreach (var c in unquotedIdentifier) if (c is >= 'A' and <= 'Z') { any = true; break; }
        if (!any) return unquotedIdentifier;
        return string.Create(unquotedIdentifier.Length, unquotedIdentifier, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++) span[i] = s[i] is >= 'A' and <= 'Z' ? (char)(s[i] + 32) : s[i];
        });
    }

    private bool NeedsQuotes(string name)
    {
        if (!IsPlain(name)) return true;
        return _needsQuotes.GetOrAdd(name, n =>
        {
            var tokens = parser.Tokenize(n);
            // Unreserved and column-name keywords are fine as table and column names. Reserved words, and the
            // "type or function name" words (left, join, inner...), are not.
            return tokens is [{ Kind: SqlTokenKind.Keyword, Keyword: KeywordClass.Reserved or KeywordClass.TypeOrFunctionName }];
        });
    }

    /// <summary>[a-z_][a-z0-9_$]*, ASCII only: the safe subset that folds to itself in every encoding.</summary>
    private static bool IsPlain(string name)
    {
        if (name.Length == 0) return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var ok = c is >= 'a' and <= 'z' or '_' || (i > 0 && (c is >= '0' and <= '9' or '$'));
            if (!ok) return false;
        }
        return true;
    }
}
