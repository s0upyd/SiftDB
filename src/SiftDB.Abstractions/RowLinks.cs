namespace SiftDB.Abstractions;

/// <summary>Where a result column's values come from: a column of a table (design section 14, F4). Names as stored.</summary>
public sealed record ColumnOrigin(string Schema, string Table, string Column);

/// <summary>
/// Some rows of a table, picked by the values of some of its columns: a foreign key's target (its referenced columns and
/// a row's values in the key), or the rows that reference one. <paramref name="Values"/> are as the grid shows them
/// (text); the provider lets the database read them as the columns' own types.
/// </summary>
public sealed record RowKey(string Schema, string Table, IReadOnlyList<string> Columns, IReadOnlyList<string> Values);

/// <param name="Values">As the reader returned them (null for NULL); the engine writes them as the grid would.</param>
/// <param name="DataTypes">Each column's type name, as the reader reports it.</param>
/// <param name="CommittedOnly">Read on a side connection, so the session's own uncommitted changes are not in it.</param>
public sealed record LookedUpRow(IReadOnlyList<string> Columns, IReadOnlyList<object?> Values, IReadOnlyList<string> DataTypes, bool CommittedOnly);
