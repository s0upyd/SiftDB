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

/// <summary>
/// A statement that reads a whole table to answer (design section 14, F19 step 4): DynamoDB runs a SELECT whose WHERE
/// doesn't fix the partition key as a full Scan, which reads every item and uses (and bills) read capacity for all of
/// them, however few match.
/// </summary>
/// <param name="Index">The index it reads through, or null for the table itself.</param>
/// <param name="PartitionKey">The key whose <c>=</c> or <c>IN</c> condition would have avoided the scan.</param>
/// <param name="EstimatedItems">The table's item count as the database last worked it out (no read), or null.</param>
public sealed record FullScan(string Table, string? Index, string PartitionKey, long? EstimatedItems);
