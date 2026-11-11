namespace SiftDB.Abstractions;

// Provider-neutral description of a database's structure. Names are exactly as stored (case preserved), because
// the identifier resolver (design section 3) matches them case-insensitively and decides on quoting itself.

public enum RelationKind { Table, PartitionedTable, View, MaterializedView, ForeignTable }

/// <summary>All user-visible schemas of one database. System schemas are left out.</summary>
public sealed record DatabaseSchema(IReadOnlyList<SchemaInfo> Schemas);

public sealed record SchemaInfo(string Name, IReadOnlyList<RelationInfo> Relations);

public sealed record RelationInfo(
    string Name,
    RelationKind Kind,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<IndexInfo> Indexes);

/// <param name="DataType">The type as the database prints it, e.g. <c>character varying(50)</c>.</param>
/// <param name="Default">The default expression's text, or null.</param>
public sealed record ColumnInfo(string Name, string DataType, bool IsNullable, string? Default);

public sealed record ForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedRelation,
    IReadOnlyList<string> ReferencedColumns);

/// <param name="Columns">Plain column members only; expression members are omitted.</param>
public sealed record IndexInfo(string Name, IReadOnlyList<string> Columns, bool IsUnique);
