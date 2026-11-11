using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// <see cref="ISqlParser"/> over libpg_query (the pgsqlparser package). The wrapper's types stay inside this class.
/// libpg_query has no error recovery: it rejects the whole input for an unterminated string, quoted identifier or
/// comment. We work around that by scanning the text before the error position and reporting the rest as one
/// <see cref="SqlTokenKind.Invalid"/> token.
/// </summary>
public sealed class PostgresSqlParser : ISqlParser
{
    public IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        if (sql.Length == 0) return [];

        var scan = PgSqlParser.Parser.Scan(sql);
        if (scan.Error is null) return Map(scan.Value!, sql);

        var bad = ErrorOffset(scan.Error, sql);
        var tokens = new List<SqlToken>();
        if (bad > 0)
        {
            var prefix = PgSqlParser.Parser.Scan(sql[..bad]);
            if (prefix.Error is not null) bad = 0; // could not isolate the bad token; call it all invalid
            else tokens.AddRange(Map(prefix.Value!, sql));
        }
        tokens.Add(new SqlToken(bad, sql.Length, SqlTokenKind.Invalid));
        return tokens;
    }

    public IReadOnlyList<SqlRange> Split(string sql)
    {
        var tokens = Tokenize(sql);
        var invalid = tokens.Count > 0 && tokens[^1].Kind == SqlTokenKind.Invalid;

        // With an invalid tail, the wrapper can only split what precedes the last ';'. The tail is one statement.
        var splitEnd = sql.Length;
        var tailStart = -1;
        if (invalid)
        {
            var lastSemi = -1;
            for (var i = tokens.Count - 2; i >= 0; i--)
                if (IsSemicolon(tokens[i], sql)) { lastSemi = i; break; }
            splitEnd = lastSemi < 0 ? 0 : tokens[lastSemi].End;
            tailStart = splitEnd;
        }

        var ranges = new List<SqlRange>();
        if (splitEnd > 0)
        {
            var text = sql[..splitEnd];
            // The parser-based split understands things the token-based one does not (BEGIN ATOMIC bodies) but
            // rejects scripts with any syntax error, e.g. a half-typed statement. Fall back to the scanner then.
            var split = PgSqlParser.Parser.SplitWithParser(text);
            if (split.Error is not null) split = PgSqlParser.Parser.SplitWithScanner(text);
            if (split.Error is null)
            {
                foreach (var s in split.Value!.Statements)
                    if (Trim(sql, tokens, s.Location, s.Location + s.Length) is { } r) ranges.Add(r);
            }
            else if (Trim(sql, tokens, 0, splitEnd) is { } whole)
            {
                ranges.Add(whole); // splitter refused it; better one big statement than none
            }
        }
        if (invalid && Trim(sql, tokens, tailStart, sql.Length) is { } tail) ranges.Add(tail);

        // The wrapper's splitter silently drops an unfinished statement in some cases (an open parenthesis, as in
        // "insert into t ("). If anything meaningful lies beyond what it returned, trust our own token split instead.
        return Covers(ranges, tokens, sql) ? ranges : SplitByTokens(sql, tokens);
    }

    private static bool Covers(List<SqlRange> ranges, IReadOnlyList<SqlToken> tokens, string sql)
    {
        var lastSignificant = -1;
        foreach (var t in tokens) if (IsSignificant(t, sql)) lastSignificant = t.End;
        if (lastSignificant < 0) return true;
        return ranges.Count > 0 && ranges[^1].End >= lastSignificant;
    }

    private static bool IsSignificant(SqlToken t, string sql) =>
        t.Kind != SqlTokenKind.Comment && !IsSemicolon(t, sql) && !IsUnterminatedComment(t, sql);

    private static bool IsUnterminatedComment(SqlToken t, string sql) =>
        t.Kind == SqlTokenKind.Invalid && string.CompareOrdinal(sql, t.Start, "/*", 0, 2) == 0;

    /// <summary>Splits at every ';' token, ignoring nesting. Used only when the wrapper's splitter cannot be trusted.</summary>
    private static List<SqlRange> SplitByTokens(string sql, IReadOnlyList<SqlToken> tokens)
    {
        var ranges = new List<SqlRange>();
        int first = -1, last = -1;

        void Close()
        {
            if (first >= 0) ranges.Add(new SqlRange(first, last - first));
            first = last = -1;
        }

        foreach (var t in tokens)
        {
            if (IsSemicolon(t, sql)) { Close(); continue; }
            if (!IsSignificant(t, sql)) continue;
            if (first < 0) first = t.Start;
            last = t.End;
        }
        Close();
        return ranges;
    }

    public SqlSyntaxError? Validate(string statement)
    {
        var r = PgSqlParser.Parser.Parse(statement, new PgSqlParser.ParserOptions());
        return r.Error is null ? null : new SqlSyntaxError(r.Error.Message ?? "Syntax error", ErrorOffset(r.Error, statement));
    }

    private static int ErrorOffset(PgSqlParser.Error error, string sql) =>
        error.CursorPos <= 0 ? 0 : Math.Clamp(error.GetCursorCharOffset(sql), 0, sql.Length);

    private static bool IsSemicolon(SqlToken t, string sql) => t.Kind == SqlTokenKind.Punctuation && sql[t.Start] == ';';

    /// <summary>Shrinks [start, end) to the first and last non-comment, non-terminator tokens inside it.</summary>
    private static SqlRange? Trim(string sql, IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var first = -1;
        var last = -1;
        foreach (var t in tokens)
        {
            if (t.End <= start) continue;
            if (t.Start >= end) break;
            if (t.Kind == SqlTokenKind.Comment) continue;
            if (IsSemicolon(t, sql)) continue;
            if (IsUnterminatedComment(t, sql)) continue;
            if (first < 0) first = t.Start;
            last = t.End;
        }
        return first < 0 ? null : new SqlRange(first, last - first);
    }

    private static List<SqlToken> Map(PgSqlParser.ScanResult scan, string sql)
    {
        var list = new List<SqlToken>(scan.Tokens.Count);
        foreach (var t in scan.Tokens)
            list.Add(new SqlToken(t.Start, t.End, KindOf(t, sql), ClassOf(t)));
        return list;
    }

    private static KeywordClass ClassOf(PgSqlParser.ScanToken t) => t.KeywordKind.ToString() switch
    {
        "UnreservedKeyword" => KeywordClass.Unreserved,
        "ColNameKeyword" => KeywordClass.ColumnName,
        "TypeFuncNameKeyword" => KeywordClass.TypeOrFunctionName,
        "ReservedKeyword" => KeywordClass.Reserved,
        _ => KeywordClass.None,
    };

    private static SqlTokenKind KindOf(PgSqlParser.ScanToken t, string sql)
    {
        if (ClassOf(t) != KeywordClass.None) return SqlTokenKind.Keyword;
        var name = t.Token.ToString();
        switch (name)
        {
            case "Ident": return sql[t.Start] == '"' ? SqlTokenKind.QuotedIdentifier : SqlTokenKind.Identifier;
            case "Uident": return SqlTokenKind.QuotedIdentifier;
            case "Sconst": case "Usconst": case "Bconst": case "Xconst": return SqlTokenKind.String;
            case "Iconst": case "Fconst": return SqlTokenKind.Number;
            case "Param": return SqlTokenKind.Parameter;
            case "SqlComment": case "CComment": return SqlTokenKind.Comment;
            case "Typecast": case "DotDot": case "ColonEquals": case "EqualsGreater":
            case "LessEquals": case "GreaterEquals": case "NotEquals": case "Op":
                return SqlTokenKind.Operator;
        }
        if (name.StartsWith("Ascii", StringComparison.Ordinal) && int.TryParse(name.AsSpan(5), out var code))
            return ",;()[].:".Contains((char)code) ? SqlTokenKind.Punctuation : SqlTokenKind.Operator;
        return SqlTokenKind.Other;
    }
}
