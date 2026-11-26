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

    /// <summary>
    /// A table's columns are all known, so a name that is none of them is worth a warning ("No column x in t"). False
    /// where any row can carry any attribute (DynamoDB): the schema then lists only what is known so far. True by
    /// default. (Contract v17; an init property, so the constructor is unchanged.)
    /// </summary>
    public bool KnownColumnsOnly { get; init; } = true;
}
