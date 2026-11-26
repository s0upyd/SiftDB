using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// DynamoDB's PartiQL (design section 14, F19): a small lexer and statement splitter of its own, since there is no
/// libpg_query for it. Enough for colouring, statements, the read-only check and the engine's token-based features
/// (completion, the resolver). There is no grammar check: DynamoDB reports a statement it can't read when it runs.
/// Like every parser here it never fails: an unterminated string, name or comment runs to the end of the text.
/// </summary>
internal sealed class PartiqlParser : ISqlParser
{
    /// <summary>
    /// PartiQL's words as DynamoDB uses them. They are also what must be quoted when used as a name (a table called
    /// "value" is written <c>"value"</c>).
    /// </summary>
    internal static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "and", "or", "not", "in", "between", "is", "null", "missing", "true", "false",
        "insert", "into", "value", "update", "set", "remove", "delete", "returning", "all", "old", "new", "modified",
        "order", "by", "asc", "desc", "exists", "list", "bag", "date", "time", "timestamp", "like", "escape",
        "case", "when", "then", "else", "end", "cast", "as", "at", "with", "values", "distinct", "having", "group",
        "limit", "offset", "join", "on", "union", "intersect", "except", "pivot", "unpivot",
    };

    public IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Comment));
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) { tokens.Add(new SqlToken(start, sql.Length, SqlTokenKind.Invalid)); break; }
                i = close + 2;
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Comment));
            }
            else if (c is '\'' or '"' or '`')
            {
                // '' and "" are a quote inside; an Ion literal in backticks is a value, like a string.
                if (!SkipQuoted(sql, ref i, c)) { tokens.Add(new SqlToken(start, sql.Length, SqlTokenKind.Invalid)); break; }
                tokens.Add(new SqlToken(start, i, c == '"' ? SqlTokenKind.QuotedIdentifier : SqlTokenKind.String));
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(sql, i + 1))))
            {
                while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                if (Peek(sql, i) == '.') { i++; while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++; }
                if (Peek(sql, i) is 'e' or 'E' && (char.IsAsciiDigit(Peek(sql, i + 1)) || (Peek(sql, i + 1) is '+' or '-' && char.IsAsciiDigit(Peek(sql, i + 2)))))
                {
                    i += 2;
                    while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                }
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Number));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                var word = sql.AsSpan(start, i - start).ToString();
                tokens.Add(Keywords.Contains(word)
                    ? new SqlToken(start, i, SqlTokenKind.Keyword, KeywordClass.Reserved)
                    : new SqlToken(start, i, SqlTokenKind.Identifier));
            }
            else if (c == '?')
            {
                i++;
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Parameter));
            }
            else if ("()[]{},;:.".Contains(c))
            {
                i++;
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Punctuation));
            }
            else if ("<>=!|+-*/%".Contains(c))
            {
                i++;
                if ((c is '<' or '>' or '!' && Peek(sql, i) == '=') || (c == '<' && Peek(sql, i) == '>') || (c == '|' && Peek(sql, i) == '|')) i++;
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Operator));
            }
            else
            {
                i++;
                tokens.Add(new SqlToken(start, i, SqlTokenKind.Other));
            }
        }
        return tokens;
    }

    /// <summary>Statements end at a <c>;</c> outside strings, names and comments; empty and comment-only ones are left out.</summary>
    public IReadOnlyList<SqlRange> Split(string sql)
    {
        var ranges = new List<SqlRange>();
        int? first = null;
        var last = 0;
        foreach (var t in Tokenize(sql))
        {
            if (t.Kind == SqlTokenKind.Punctuation && sql[t.Start] == ';')
            {
                if (first is { } f) ranges.Add(new SqlRange(f, last - f));
                first = null;
                continue;
            }
            if (t.Kind == SqlTokenKind.Comment) continue;
            first ??= t.Start;
            last = t.End;
        }
        if (first is { } s) ranges.Add(new SqlRange(s, last - s));
        return ranges;
    }

    /// <summary>No PartiQL grammar here: DynamoDB says what it can't read when the statement runs.</summary>
    public SqlSyntaxError? Validate(string statement) => null;

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>Past the closing quote, doubled quotes inside; false when it never closes.</summary>
    private static bool SkipQuoted(string s, ref int i, char quote)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == quote)
            {
                if (Peek(s, i + 1) == quote && quote != '`') { i += 2; continue; }
                i++;
                return true;
            }
            i++;
        }
        return false;
    }

    /// <summary>The first word of a statement, lower case ("select", "update"…), or null.</summary>
    internal string? Verb(string statement)
    {
        foreach (var t in Tokenize(statement))
        {
            if (t.Kind == SqlTokenKind.Comment) continue;
            return t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier ? statement[t.Start..t.End].ToLowerInvariant() : null;
        }
        return null;
    }
}

/// <summary>
/// DynamoDB's names (design section 3): case-sensitive and never folded. A name is written bare when it is a plain
/// word that isn't a PartiQL keyword, else in double quotes (a table called <c>orders-dev</c> is <c>"orders-dev"</c>).
/// </summary>
internal sealed class DynamoIdentifiers : IIdentifierPolicy
{
    public string Quote(string identifier)
    {
        var plain = identifier.Length > 0 && (char.IsAsciiLetter(identifier[0]) || identifier[0] == '_')
            && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            && !PartiqlParser.Keywords.Contains(identifier);
        return plain ? identifier : "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public string Fold(string unquotedIdentifier) => unquotedIdentifier;
}
