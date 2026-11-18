namespace SiftDB.Abstractions;

public enum WholeTableAction { Update, Delete, Truncate }

/// <summary>
/// A statement that changes every row of a table: an UPDATE or DELETE with no WHERE, or a TRUNCATE (design section 14,
/// F1). <paramref name="Schema"/> is null when the statement does not name one; names are as the database folds them.
/// </summary>
public readonly record struct WholeTableChange(WholeTableAction Action, string? Schema, string Table);

/// <summary>What a session knows about a table, for the missing-WHERE warning.</summary>
/// <param name="Temporary">A temporary table (only this session sees it): no warning.</param>
/// <param name="EstimatedRows">The database's own estimate, with no scan, or null when it has none.</param>
public sealed record TableFacts(bool Temporary, long? EstimatedRows);
