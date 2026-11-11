using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Reads the structure from pg_catalog with four queries (relations, columns, constraints, indexes), however many
/// tables there are. Partitions are left out, so a table with thousands of partitions shows as one entry.
/// </summary>
internal static class PostgresIntrospector
{
    // Relations we describe, in user schemas only (pg_* are system schemas, reserved by Postgres).
    private const string Filter = """
        c.relkind in ('r','p','v','m','f') and not c.relispartition
        and n.nspname <> 'information_schema' and n.nspname !~ '^pg_'
        """;

    private sealed class Builder(string schema, string name, RelationKind kind)
    {
        public string Schema = schema;
        public string Name = name;
        public RelationKind Kind = kind;
        public List<ColumnInfo> Columns = new();
        public List<string> PrimaryKey = new();
        public List<ForeignKeyInfo> ForeignKeys = new();
        public List<IndexInfo> Indexes = new();
    }

    public static async Task<DatabaseSchema> ReadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var schemaNames = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "select nspname from pg_namespace n where nspname <> 'information_schema' and nspname !~ '^pg_' order by nspname", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) schemaNames.Add(r.GetString(0));

        var rels = new Dictionary<uint, Builder>();
        await using (var cmd = new NpgsqlCommand($"""
            select c.oid, n.nspname, c.relname, c.relkind::text
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where {Filter}
            order by n.nspname, c.relname
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rels[r.GetFieldValue<uint>(0)] = new Builder(r.GetString(1), r.GetString(2), KindOf(r.GetString(3)));

        await using (var cmd = new NpgsqlCommand($"""
            select a.attrelid, a.attname::text, format_type(a.atttypid, a.atttypmod), not a.attnotnull,
                   pg_get_expr(d.adbin, d.adrelid)
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid join pg_namespace n on n.oid = c.relnamespace
            left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum
            where {Filter} and a.attnum > 0 and not a.attisdropped
            order by a.attrelid, a.attnum
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (rels.TryGetValue(r.GetFieldValue<uint>(0), out var b))
                    b.Columns.Add(new ColumnInfo(r.GetString(1), r.GetString(2), r.GetBoolean(3),
                        r.IsDBNull(4) ? null : r.GetString(4)));

        await using (var cmd = new NpgsqlCommand($"""
            select con.conrelid, con.conname::text, con.contype::text,
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(con.conkey) with ordinality k(attnum, ord)
                      join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum),
                   fn.nspname::text, fc.relname::text,
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(con.confkey) with ordinality k(attnum, ord)
                      join pg_attribute a on a.attrelid = con.confrelid and a.attnum = k.attnum)
            from pg_constraint con
            join pg_class c on c.oid = con.conrelid join pg_namespace n on n.oid = c.relnamespace
            left join pg_class fc on fc.oid = con.confrelid
            left join pg_namespace fn on fn.oid = fc.relnamespace
            where con.contype in ('p','f') and {Filter}
            order by con.conrelid, con.conname
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!rels.TryGetValue(r.GetFieldValue<uint>(0), out var b)) continue;
                var cols = Names(r, 3);
                if (r.GetString(2) == "p") b.PrimaryKey.AddRange(cols);
                else b.ForeignKeys.Add(new ForeignKeyInfo(r.GetString(1), cols, r.GetString(4), r.GetString(5), Names(r, 6)));
            }

        await using (var cmd = new NpgsqlCommand($"""
            select i.indrelid, ic.relname::text, i.indisunique,
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(i.indkey::int2[]) with ordinality k(attnum, ord)
                      join pg_attribute a on a.attrelid = i.indrelid and a.attnum = k.attnum)
            from pg_index i
            join pg_class ic on ic.oid = i.indexrelid
            join pg_class c on c.oid = i.indrelid join pg_namespace n on n.oid = c.relnamespace
            where {Filter} and i.indisvalid
            order by i.indrelid, ic.relname
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (rels.TryGetValue(r.GetFieldValue<uint>(0), out var b))
                    b.Indexes.Add(new IndexInfo(r.GetString(1), Names(r, 3), r.GetBoolean(2)));

        var bySchema = rels.Values.ToLookup(b => b.Schema);
        var schemas = schemaNames.Select(s => new SchemaInfo(s,
            bySchema[s].OrderBy(b => b.Name, StringComparer.Ordinal)
                .Select(b => new RelationInfo(b.Name, b.Kind, b.Columns, b.PrimaryKey, b.ForeignKeys, b.Indexes))
                .ToList())).ToList();
        return new DatabaseSchema(schemas);
    }

    private static string[] Names(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? [] : r.GetFieldValue<string[]>(ordinal);

    private static RelationKind KindOf(string relkind) => relkind switch
    {
        "r" => RelationKind.Table,
        "p" => RelationKind.PartitionedTable,
        "v" => RelationKind.View,
        "m" => RelationKind.MaterializedView,
        "f" => RelationKind.ForeignTable,
        _ => throw new InvalidOperationException($"Unexpected relkind '{relkind}'"),
    };
}
