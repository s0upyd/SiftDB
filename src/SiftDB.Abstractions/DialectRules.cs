namespace SiftDB.Abstractions;

/// <summary>
/// Facts about a dialect that the identifier checker (design section 3) uses to warn about SQL the server will refuse
/// with a confusing message. The defaults are permissive, so a provider that says nothing gets no such warnings.
/// </summary>
/// <param name="QualifiedSetColumns">
/// <c>UPDATE t a SET a.col = …</c> is allowed. Postgres refuses it: it reads <c>a.col</c> as field <c>col</c> of a
/// column <c>a</c> ("column "a" of relation "t" does not exist").
/// </param>
/// <param name="NumbersAsBooleans">
/// A boolean column can be compared with or set to 0 and 1 (SQL Server's <c>bit</c>). Postgres refuses it
/// ("operator does not exist: boolean = integer").
/// </param>
public sealed record DialectRules(bool QualifiedSetColumns = true, bool NumbersAsBooleans = true)
{
    public static readonly DialectRules Permissive = new();
}
