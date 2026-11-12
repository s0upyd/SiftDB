namespace SiftDB.Abstractions;

/// <summary>
/// Provider-neutral value types (design sections 4 and 10). Result styling rules and bind variables name these, never a
/// database's own types; each provider maps its types onto them.
/// </summary>
public enum GenericType
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
}
