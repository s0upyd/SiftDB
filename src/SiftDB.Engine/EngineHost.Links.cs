using System.Globalization;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Bridge;

namespace SiftDB.Engine;

/// <summary>
/// Following foreign keys (design section 14, F4). For each result, the engine works out from the columns' origins and
/// the schema cache which columns are links (the whole of a foreign key, every column of it in the result) and which
/// rows can be asked "what references this?" (every column of the table's primary key in the result). The UI only draws
/// what it is told; the lookups and the SQL that opens a row are the engine's too.
/// </summary>
public sealed partial class EngineHost
{
    /// <summary>"Referenced by" counts stop here, and say "1,000+".</summary>
    private const long ReferenceCountCap = 1_000;

    private static readonly TimeSpan ReferenceCountTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The links and keys of a result, for its "columns" event. Empty when the origins are not known.</summary>
    private static (object[] Links, object[] Keys) DescribeLinks(DatabaseSchema? schema, IReadOnlyList<ColumnOrigin?> origins)
    {
        if (schema is null || origins.Count == 0) return ([], []);
        var links = new List<object>();
        var keys = new List<object>();
        // Per table the result reads from: each of its columns' first place in the result.
        foreach (var table in origins.Select((o, i) => (o, i)).Where(x => x.o is not null).GroupBy(x => (x.o!.Schema, x.o.Table)))
        {
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (o, i) in table) at.TryAdd(o!.Column, i);
            if (Relation(schema, table.Key.Schema, table.Key.Table) is not { } rel) continue;

            foreach (var fk in rel.ForeignKeys)
            {
                if (fk.Columns.Count == 0 || !fk.Columns.All(at.ContainsKey)) continue; // a link needs the whole key
                links.Add(new
                {
                    name = fk.Name, columns = fk.Columns.Select(c => at[c]).ToArray(),
                    schema = fk.ReferencedSchema, table = fk.ReferencedRelation, targetColumns = fk.ReferencedColumns,
                });
            }
            // A row can be asked what references it through any of its table's keys that a foreign key points at: the
            // primary key, or a unique one (orders.customer_email -> customer.email), once every column of it is here.
            foreach (var key in UniqueKeys(rel))
            {
                if (!key.All(at.ContainsKey) || !ReferencingKeys(schema, table.Key.Schema, rel.Name, key).Any()) continue;
                keys.Add(new
                {
                    schema = table.Key.Schema, table = rel.Name, keyColumns = key, columns = key.Select(c => at[c]).ToArray(),
                    primary = SameColumns(key, rel.PrimaryKey),
                });
            }
        }
        return ([.. links], [.. keys]);
    }

    /// <summary>The table's primary key, then each unique index on plain columns (a unique constraint has one), each once.</summary>
    private static IEnumerable<IReadOnlyList<string>> UniqueKeys(RelationInfo rel)
    {
        var seen = new List<IReadOnlyList<string>>();
        foreach (var key in rel.Indexes.Where(i => i.IsUnique).Select(i => i.Columns).Prepend(rel.PrimaryKey))
        {
            if (key.Count == 0 || seen.Any(k => SameColumns(k, key))) continue; // an expression index has no plain columns
            seen.Add(key);
            yield return key;
        }
    }

    private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.Count == b.Count && !a.Except(b).Any();

    private static RelationInfo? Relation(DatabaseSchema schema, string schemaName, string table) =>
        schema.Schemas.FirstOrDefault(s => s.Name == schemaName)?.Relations.FirstOrDefault(r => r.Name == table);

    /// <summary>Every foreign key, in any table, that points at <paramref name="table"/> through exactly the columns of <paramref name="key"/>.</summary>
    private static IEnumerable<(string Schema, RelationInfo Child, ForeignKeyInfo Key)> ReferencingKeys(DatabaseSchema schema, string schemaName,
        string table, IReadOnlyList<string> key) =>
        from s in schema.Schemas
        from child in s.Relations
        from fk in child.ForeignKeys
        where fk.ReferencedSchema == schemaName && fk.ReferencedRelation == table && SameColumns(fk.ReferencedColumns, key)
        select (s.Name, child, fk);

    private static RowKey ReadKey(JsonElement root) => new(
        root.GetProperty("schema").GetString()!, root.GetProperty("table").GetString()!,
        root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList(),
        root.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToList());

    /// <summary>
    /// "peekRow": the referenced row, for the card shown when hovering a link. Read on the sheet's own session when it is
    /// idle (so its uncommitted rows count), otherwise on a side connection, and then the card says so.
    /// </summary>
    private async Task PeekRowAsync(int id, int connectionId, JsonElement root)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var key = ReadKey(root);
        var idle = state.SessionLock.Wait(0);
        LookedUpRow? row;
        try { row = await state.Connection.PeekRowAsync(key, idle, CancellationToken.None); }
        finally { if (idle) state.SessionLock.Release(); }
        Event(row is null
            ? new { type = "peekedRow", replyTo = id, found = false, columns = Array.Empty<string>(), values = Array.Empty<string?>(), committedOnly = false }
            : new
            {
                type = "peekedRow", replyTo = id, found = true, columns = row.Columns.ToArray(),
                values = row.Values.Select((v, i) => v is null ? null : RowBatchWriter.FormatValue(v, dateOnly: row.DataTypes[i] == "date")).ToArray(),
                committedOnly = row.CommittedOnly,
            });
    }

    /// <summary>"rowQuery": the statement that opens the rows a key picks, as it goes into the sheet. Nothing is hidden: it is ordinary SQL.</summary>
    private void RowQuery(int id, int connectionId, JsonElement root)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        Event(new { type = "rowQuery", replyTo = id, sql = RowQuerySql(state.Provider.Identifiers, ReadKey(root)) });
    }

    /// <summary><c>select * from "Customer" where "Id" = 42</c>. A number is written bare, anything else as a string literal,
    /// which the database reads as the column's type. The schema is written unless it is <c>public</c>.</summary>
    public static string RowQuerySql(IIdentifierPolicy policy, RowKey key)
    {
        var table = (key.Schema == "public" ? "" : policy.Quote(key.Schema) + ".") + policy.Quote(key.Table);
        var where = string.Join(" and ", key.Columns.Select((c, i) => $"{policy.Quote(c)} = {Literal(key.Values[i])}"));
        return $"select * from {table} where {where}";
    }

    private static string Literal(string value) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)
        && !value.StartsWith('+') && !value.EndsWith('.') && !value.StartsWith('.')
            ? value
            : "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// "referencedBy": for a row and one of its table's keys (primary or unique), every foreign key that points at it through
    /// that key, how many rows each has (counted
    /// to 1,000, and given up after 2 s), and the statement that opens them.
    /// </summary>
    private async Task ReferencedByAsync(int id, int connectionId, JsonElement root)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var key = ReadKey(root);
        if (state.Schema is not { } schema || Relation(schema, key.Schema, key.Table) is not { } rel) { Error(id, "That table is not in the schema cache."); return; }
        var valueOf = key.Columns.Select((c, i) => (c, v: key.Values[i])).ToDictionary(x => x.c, x => x.v);

        var found = await Task.WhenAll(ReferencingKeys(schema, key.Schema, rel.Name, key.Columns).Select(async r =>
        {
            var childKey = new RowKey(r.Schema, r.Child.Name, r.Key.Columns, r.Key.ReferencedColumns.Select(c => valueOf[c]).ToList());
            long? count;
            try { count = await state.Connection.CountRowsAsync(childKey, ReferenceCountCap, ReferenceCountTimeout, CancellationToken.None); }
            catch { count = null; }
            return new
            {
                schema = r.Schema, table = r.Child.Name, columns = r.Key.Columns, name = r.Key.Name,
                count = count is > ReferenceCountCap ? ReferenceCountCap : count, capped = count is > ReferenceCountCap,
                sql = RowQuerySql(state.Provider.Identifiers, childKey),
            };
        }));
        Event(new { type = "referencedBy", replyTo = id, references = found });
    }
}
