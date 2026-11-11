namespace SiftDB.Abstractions;

public enum SqlTokenKind
{
    Keyword,
    Identifier,
    QuotedIdentifier,
    String,
    Number,
    Operator,
    Punctuation,
    Comment,
    Parameter,
    /// <summary>Text the lexer rejected, typically an unterminated string, quoted identifier or comment.
    /// Runs from the start of the bad token to the end of the input.</summary>
    Invalid,
    Other,
}

/// <summary>How reserved a keyword is. Drives quoting: a reserved word used as a name must be quoted.</summary>
public enum KeywordClass
{
    None,
    Unreserved,
    ColumnName,
    TypeOrFunctionName,
    Reserved,
}

/// <summary>A token. Offsets are UTF-16 code unit offsets into the input, <see cref="End"/> exclusive.</summary>
public readonly record struct SqlToken(int Start, int End, SqlTokenKind Kind, KeywordClass Keyword = KeywordClass.None)
{
    public int Length => End - Start;
}

/// <summary>A statement's extent in the script (UTF-16, trimmed of surrounding whitespace, comments and the terminator).</summary>
public readonly record struct SqlRange(int Start, int Length)
{
    public int End => Start + Length;
}

public sealed record SqlSyntaxError(string Message, int Offset);

/// <summary>
/// Dialect-specific lexing and parsing, supplied by each provider. Everything here is designed to be called on
/// every keystroke, on text that is often half-typed: <see cref="Tokenize"/> and <see cref="Split"/> never fail.
/// </summary>
public interface ISqlParser
{
    /// <summary>Tokens for the whole text. Tolerates unterminated tokens (see <see cref="SqlTokenKind.Invalid"/>).</summary>
    IReadOnlyList<SqlToken> Tokenize(string sql);

    /// <summary>
    /// Statement extents, in order, excluding empty and comment-only statements. Tolerates incomplete
    /// statements; an unterminated token makes the final statement run to the end of the text.
    /// </summary>
    IReadOnlyList<SqlRange> Split(string sql);

    /// <summary>Checks one complete statement against the real grammar. Null means it parsed.</summary>
    SqlSyntaxError? Validate(string statement);
}
