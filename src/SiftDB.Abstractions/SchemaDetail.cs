namespace SiftDB.Abstractions;

// The richer schema explorer (design section 14, F13). The fast schema load (SchemaModel.cs) stays as it is: relations,
// columns, keys and indexes for the whole database. What is here is loaded lazily, one table (or one schema's other
// objects) at a time, the first time it is opened in the tree, on a connection of its own. Definitions are the
// database's own text (Postgres: pg_get_constraintdef, pg_get_indexdef, pg_get_triggerdef), so they read as it would
// write them.

/// <summary>Everything about one relation beyond its columns' names and types.</summary>
/// <param name="EstimatedRows">The planner's estimate (no scan), or null when there is none (never analysed).</param>
/// <param name="TotalBytes">On disk, with indexes and TOAST; null for a view.</param>
/// <param name="Partitions">A partitioned table's partitions, as "name" or "schema.name", with their bounds.</param>
/// <param name="Definition">A view's or materialized view's query.</param>
public sealed record RelationDetail(
    string? Comment,
    long? EstimatedRows,
    long? TotalBytes,
    IReadOnlyList<ColumnDetail> Columns,
    IReadOnlyList<KeyConstraint> Keys,
    IReadOnlyList<ForeignKeyDetail> ForeignKeys,
    IReadOnlyList<IndexDetail> Indexes,
    IReadOnlyList<CheckConstraint> Checks,
    IReadOnlyList<TriggerInfo> Triggers,
    IReadOnlyList<PartitionInfo> Partitions,
    string? Definition);

/// <param name="Identity">"always" or "by default" for an identity column, else null.</param>
/// <param name="Generated">The expression of a generated column, else null.</param>
public sealed record ColumnDetail(string Name, string? Comment, string? Identity, string? Generated);

/// <summary>The primary key or a unique constraint.</summary>
public sealed record KeyConstraint(string Name, bool Primary, IReadOnlyList<string> Columns, string Definition);

/// <param name="OnDelete">The action, e.g. "cascade", or null for the default (no action).</param>
public sealed record ForeignKeyDetail(
    string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedRelation, IReadOnlyList<string> ReferencedColumns,
    string? OnDelete, string? OnUpdate, bool Deferrable, string Definition);

/// <param name="Method">btree, gin, gist, hash, brin…</param>
/// <param name="Keys">What it indexes, as written in its definition: columns or expressions.</param>
/// <param name="Predicate">A partial index's WHERE, else null.</param>
/// <param name="Constraint">The key or exclusion constraint it backs, else null.</param>
public sealed record IndexDetail(
    string Name, string Method, bool Unique, bool Primary, string Keys, string? Predicate, long? Bytes, string? Constraint, string Definition);

/// <param name="Exclusion">An exclusion constraint rather than a check.</param>
public sealed record CheckConstraint(string Name, bool Exclusion, string Definition);

/// <param name="Timing">"before", "after" or "instead of".</param>
/// <param name="Events">"insert", "update", "delete", "truncate", in that order.</param>
/// <param name="ForEachRow">A row trigger, else a statement trigger.</param>
public sealed record TriggerInfo(string Name, string Timing, IReadOnlyList<string> Events, bool ForEachRow, string Function, bool Enabled, string Definition);

/// <param name="Bound">The partition bound, e.g. <c>FOR VALUES FROM ('2026-01-01') TO ('2026-02-01')</c>.</param>
public sealed record PartitionInfo(string Schema, string Name, string Bound);

/// <summary>A schema's objects other than relations, loaded when the schema is opened.</summary>
public sealed record SchemaObjects(IReadOnlyList<FunctionInfo> Functions, IReadOnlyList<SequenceInfo> Sequences, IReadOnlyList<TypeInfo> Types);

/// <param name="Kind">"function", "procedure", "aggregate" or "window".</param>
/// <param name="Arguments">The arguments as they identify it (overloads differ here), e.g. <c>integer, text</c>.</param>
/// <param name="Returns">What it returns, e.g. <c>SETOF orders</c>; null for a procedure.</param>
public sealed record FunctionInfo(string Name, string Kind, string Arguments, string? Returns, string? Comment);

public sealed record SequenceInfo(string Name, string DataType, string? OwnedBy);

/// <param name="Kind">"enum", "domain", "composite" or "range".</param>
/// <param name="Values">An enum's labels in order; empty for other kinds.</param>
/// <param name="Detail">A domain's base type and constraints, a composite type's fields, a range's subtype.</param>
public sealed record TypeInfo(string Name, string Kind, IReadOnlyList<string> Values, string? Detail, string? Comment);

/// <summary>Which kind of object a script is for.</summary>
public enum ObjectKind { Relation, Function, Sequence, Type }

/// <summary>A database object a script names. <paramref name="Arguments"/> picks a function among its overloads.</summary>
public sealed record ObjectRef(ObjectKind Kind, string Schema, string Name, string? Arguments = null);
