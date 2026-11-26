using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// The schema explorer's details (design section 14, F13), read from pg_catalog one relation, or one schema's other
/// objects, at a time. Definitions are Postgres's own (pg_get_constraintdef, pg_get_indexdef, pg_get_triggerdef,
/// pg_get_viewdef, pg_get_functiondef), so they read as Postgres would write them. Internal triggers (those behind
/// foreign keys) are left out.
/// </summary>
internal static class PostgresDetail
{
    private const string RelationOid = """
        (select c.oid from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace
         where n.nspname = @schema and c.relname = @name)
        """;

    private static NpgsqlCommand Command(NpgsqlConnection conn, string sql, string schema, string? name = null)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("schema", NpgsqlTypes.NpgsqlDbType.Text) { Value = schema });
        if (name is not null) cmd.Parameters.Add(new NpgsqlParameter("name", NpgsqlTypes.NpgsqlDbType.Text) { Value = name });
        return cmd;
    }

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string[] Names(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? [] : r.GetFieldValue<string[]>(i);

    public static async Task<RelationDetail?> RelationAsync(NpgsqlConnection conn, string schema, string name, CancellationToken ct)
    {
        string? comment, definition;
        long? rows, bytes;
        await using (var cmd = Command(conn, $"""
            select obj_description(c.oid, 'pg_class'), c.reltuples::float8,
                   case when c.relkind in ('r', 'm', 'p', 't') then pg_total_relation_size(c.oid) end,
                   case when c.relkind in ('v', 'm') then pg_get_viewdef(c.oid, true) end
            from pg_catalog.pg_class c where c.oid = {RelationOid}
            """, schema, name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            comment = Str(r, 0);
            rows = r.GetDouble(1) is var t && t >= 0 ? (long)Math.Round(t) : null;
            bytes = r.IsDBNull(2) ? null : r.GetInt64(2);
            definition = Str(r, 3)?.Trim();
        }

        var columns = new List<ColumnDetail>();
        await using (var cmd = Command(conn, $"""
            select a.attname::text, col_description(a.attrelid, a.attnum),
                   case a.attidentity when 'a' then 'always' when 'd' then 'by default' end,
                   case when a.attgenerated <> '' then pg_get_expr(d.adbin, d.adrelid) end
            from pg_catalog.pg_attribute a
            left join pg_catalog.pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum
            where a.attrelid = {RelationOid} and a.attnum > 0 and not a.attisdropped
            order by a.attnum
            """, schema, name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) columns.Add(new ColumnDetail(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3)));

        var keys = new List<KeyConstraint>();
        var fks = new List<ForeignKeyDetail>();
        var checks = new List<CheckConstraint>();
        await using (var cmd = Command(conn, $"""
            select con.conname::text, con.contype::text, pg_get_constraintdef(con.oid, true),
                   (select array_agg(a.attname::text order by k.ord) from unnest(con.conkey) with ordinality k(attnum, ord)
                      join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum),
                   fn.nspname::text, fc.relname::text,
                   (select array_agg(a.attname::text order by k.ord) from unnest(con.confkey) with ordinality k(attnum, ord)
                      join pg_catalog.pg_attribute a on a.attrelid = con.confrelid and a.attnum = k.attnum),
                   con.confdeltype::text, con.confupdtype::text, con.condeferrable
            from pg_catalog.pg_constraint con
            left join pg_catalog.pg_class fc on fc.oid = con.confrelid
            left join pg_catalog.pg_namespace fn on fn.oid = fc.relnamespace
            where con.conrelid = {RelationOid} and con.contype in ('p', 'u', 'f', 'c', 'x')
            order by con.contype, con.conname
            """, schema, name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var (n, kind, def) = (r.GetString(0), r.GetString(1), r.GetString(2));
                switch (kind)
                {
                    case "p" or "u": keys.Add(new KeyConstraint(n, kind == "p", Names(r, 3), def)); break;
                    case "f":
                        fks.Add(new ForeignKeyDetail(n, Names(r, 3), r.GetString(4), r.GetString(5), Names(r, 6),
                            Action(r.GetString(7)), Action(r.GetString(8)), r.GetBoolean(9), def));
                        break;
                    default: checks.Add(new CheckConstraint(n, kind == "x", def)); break;
                }
            }
        keys.Sort((a, b) => a.Primary == b.Primary ? string.CompareOrdinal(a.Name, b.Name) : a.Primary ? -1 : 1);
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            foreach (var ck in checks)
                if (!ck.Exclusion && LengthLimits(ck.Definition).TryGetValue(col.Name, out var max) && (col.MaxLength is null || max < col.MaxLength))
                    col = col with { MaxLength = max, LengthCheck = ck.Name };
            columns[i] = col;
        }

        var indexes = new List<IndexDetail>();
        await using (var cmd = Command(conn, $"""
            select ic.relname::text, am.amname::text, i.indisunique, i.indisprimary, pg_get_indexdef(i.indexrelid, 0, true),
                   pg_get_expr(i.indpred, i.indrelid), pg_relation_size(i.indexrelid),
                   (select con.conname::text from pg_catalog.pg_constraint con where con.conindid = i.indexrelid and con.conrelid = i.indrelid limit 1)
            from pg_catalog.pg_index i
            join pg_catalog.pg_class ic on ic.oid = i.indexrelid
            join pg_catalog.pg_am am on am.oid = ic.relam
            where i.indrelid = {RelationOid}
            order by i.indisprimary desc, ic.relname
            """, schema, name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var def = r.GetString(4);
                indexes.Add(new IndexDetail(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), IndexKeys(def), Unwrap(Str(r, 5)),
                    r.IsDBNull(6) ? null : r.GetInt64(6), Str(r, 7), def));
            }

        var triggers = new List<TriggerInfo>();
        await using (var cmd = Command(conn, $"""
            select t.tgname::text, t.tgtype, t.tgfoid::regproc::text, t.tgenabled <> 'D', pg_get_triggerdef(t.oid, true)
            from pg_catalog.pg_trigger t
            where t.tgrelid = {RelationOid} and not t.tgisinternal
            order by t.tgname
            """, schema, name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                // tgtype's bits (pg_trigger.h): 1 row, 2 before, 4 insert, 8 delete, 16 update, 32 truncate, 64 instead.
                int type = r.GetInt16(1);
                var events = new List<string>();
                if ((type & 4) != 0) events.Add("insert");
                if ((type & 16) != 0) events.Add("update");
                if ((type & 8) != 0) events.Add("delete");
                if ((type & 32) != 0) events.Add("truncate");
                var timing = (type & 64) != 0 ? "instead of" : (type & 2) != 0 ? "before" : "after";
                triggers.Add(new TriggerInfo(r.GetString(0), timing, events, (type & 1) != 0, r.GetString(2), r.GetBoolean(3), r.GetString(4)));
            }

        var partitions = new List<PartitionInfo>();
        await using (var cmd = Command(conn, $"""
            select n.nspname::text, c.relname::text, pg_get_expr(c.relpartbound, c.oid)
            from pg_catalog.pg_inherits i
            join pg_catalog.pg_class c on c.oid = i.inhrelid join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where i.inhparent = {RelationOid} and c.relispartition
            order by c.relname
            """, schema, name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) partitions.Add(new PartitionInfo(r.GetString(0), r.GetString(1), Str(r, 2) ?? ""));

        return new RelationDetail(comment, rows, bytes, columns, keys, fks, indexes, checks, triggers, partitions, definition);
    }

    /// <summary>An expression without the one pair of parentheses Postgres puts round the whole of it, if it does.</summary>
    private static string? Unwrap(string? e)
    {
        if (e is not { Length: > 1 } || e[0] != '(' || e[^1] != ')') return e;
        var depth = 0;
        for (var i = 0; i < e.Length; i++)
        {
            if (e[i] == '(') depth++;
            else if (e[i] == ')' && --depth == 0 && i < e.Length - 1) return e; // "(a) and (b)": the first pair closes early
        }
        return e[1..^1];
    }

    private const string LengthOf = @"\b(?:char_length|character_length|length)\((?<col>""(?:[^""]|"""")+""|\w+)(?:::[\w ]+)?\)";
    private static readonly Regex AtMost = new($@"{LengthOf}\s*(?<op><=|<)\s*(?<n>\d+)\b", RegexOptions.CultureInvariant);
    private static readonly Regex AtMostReversed = new($@"(?<!\w)(?<n>\d+)\s*(?<op>>=|>)\s*{LengthOf}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The most characters a check allows each column, from Postgres's text for it: <c>length(a) &lt;= 100</c>,
    /// <c>char_length(a) &lt; 50</c>, <c>100 &gt;= length(a)</c>, a BETWEEN's upper end, and several ANDed together. A check
    /// with OR or NOT in it may not limit the column at all, so it gives none.
    /// </summary>
    internal static Dictionary<string, int> LengthLimits(string definition)
    {
        var limits = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Regex.IsMatch(definition, @"\b(?:OR|NOT)\b")) return limits;
        foreach (var m in AtMost.Matches(definition).Concat(AtMostReversed.Matches(definition)))
        {
            if (!int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) continue;
            var max = m.Groups["op"].Value is "<" or ">" ? n - 1 : n;
            var col = m.Groups["col"].Value;
            if (col.StartsWith('"')) col = col[1..^1].Replace("\"\"", "\"");
            if (max >= 0 && (!limits.TryGetValue(col, out var was) || max < was)) limits[col] = max;
        }
        return limits;
    }

    /// <summary>"cascade", "set null"… for an action other than the default; null for "no action".</summary>
    private static string? Action(string code) => code switch
    {
        "r" => "restrict", "c" => "cascade", "n" => "set null", "d" => "set default", _ => null,
    };

    /// <summary>What an index covers, from its definition: the part in parentheses after USING method.</summary>
    private static string IndexKeys(string definition)
    {
        var at = definition.IndexOf(" USING ", StringComparison.Ordinal);
        var open = at < 0 ? -1 : definition.IndexOf('(', at);
        if (open < 0) return "";
        var depth = 0;
        for (var i = open; i < definition.Length; i++)
        {
            if (definition[i] == '(') depth++;
            else if (definition[i] == ')' && --depth == 0) return definition[(open + 1)..i];
        }
        return definition[(open + 1)..];
    }

    public static async Task<SchemaObjects> ObjectsAsync(NpgsqlConnection conn, string schema, CancellationToken ct)
    {
        var functions = new List<FunctionInfo>();
        await using (var cmd = Command(conn, """
            select p.proname::text, p.prokind::text, pg_get_function_identity_arguments(p.oid),
                   case when p.prokind <> 'p' then pg_get_function_result(p.oid) end, obj_description(p.oid, 'pg_proc')
            from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace
            where n.nspname = @schema
              and not exists (select 1 from pg_catalog.pg_depend d where d.objid = p.oid and d.deptype = 'e')
            order by p.proname, 3
            """, schema))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                functions.Add(new FunctionInfo(r.GetString(0), r.GetString(1) switch { "p" => "procedure", "a" => "aggregate", "w" => "window", _ => "function" },
                    r.GetString(2), Str(r, 3), Str(r, 4)));

        var sequences = new List<SequenceInfo>();
        await using (var cmd = Command(conn, """
            select c.relname::text, format_type(s.seqtypid, null),
                   (select tc.relname || '.' || a.attname from pg_catalog.pg_depend d
                      join pg_catalog.pg_class tc on tc.oid = d.refobjid
                      join pg_catalog.pg_attribute a on a.attrelid = d.refobjid and a.attnum = d.refobjsubid
                    where d.objid = c.oid and d.deptype in ('a', 'i') limit 1)
            from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            join pg_catalog.pg_sequence s on s.seqrelid = c.oid
            where n.nspname = @schema and c.relkind = 'S'
            order by c.relname
            """, schema))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) sequences.Add(new SequenceInfo(r.GetString(0), r.GetString(1), Str(r, 2)));

        var types = new List<TypeInfo>();
        await using (var cmd = Command(conn, """
            select t.typname::text, t.typtype::text,
                   (select array_agg(e.enumlabel::text order by e.enumsortorder) from pg_catalog.pg_enum e where e.enumtypid = t.oid),
                   case t.typtype
                     when 'd' then format_type(t.typbasetype, t.typtypmod)
                       || coalesce(' ' || (select string_agg(pg_get_constraintdef(c.oid, true), ' ') from pg_catalog.pg_constraint c where c.contypid = t.oid), '')
                     when 'c' then (select string_agg(a.attname || ' ' || format_type(a.atttypid, a.atttypmod), ', ' order by a.attnum)
                                    from pg_catalog.pg_attribute a where a.attrelid = t.typrelid and a.attnum > 0 and not a.attisdropped)
                     when 'r' then (select format_type(rg.rngsubtype, null) from pg_catalog.pg_range rg where rg.rngtypid = t.oid)
                   end,
                   obj_description(t.oid, 'pg_type')
            from pg_catalog.pg_type t join pg_catalog.pg_namespace n on n.oid = t.typnamespace
            where n.nspname = @schema
              and (t.typtype in ('e', 'd', 'r')
                   or (t.typtype = 'c' and (select c.relkind from pg_catalog.pg_class c where c.oid = t.typrelid) = 'c'))
              and not exists (select 1 from pg_catalog.pg_depend d where d.objid = t.oid and d.deptype = 'e')
            order by t.typname
            """, schema))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                types.Add(new TypeInfo(r.GetString(0), r.GetString(1) switch { "e" => "enum", "d" => "domain", "c" => "composite", _ => "range" },
                    Names(r, 2), Str(r, 3), Str(r, 4)));

        return new SchemaObjects(functions, sequences, types);
    }

    // ---- scripts ----------------------------------------------------------------------------------------------------

    public static async Task<string?> ScriptAsync(NpgsqlConnection conn, IIdentifierPolicy policy, ObjectRef obj, bool create, CancellationToken ct)
    {
        var name = policy.Quote(obj.Schema) + "." + policy.Quote(obj.Name);
        switch (obj.Kind)
        {
            case ObjectKind.Relation:
            {
                await using var cmd = Command(conn, $"select c.relkind::text from pg_catalog.pg_class c where c.oid = {RelationOid}", obj.Schema, obj.Name);
                if (await cmd.ExecuteScalarAsync(ct) is not string kind) return null;
                var what = kind switch { "v" => "VIEW", "m" => "MATERIALIZED VIEW", "f" => "FOREIGN TABLE", _ => "TABLE" };
                if (!create) return $"DROP {what} {name};";
                return kind switch
                {
                    "v" or "m" => await ViewScriptAsync(conn, policy, obj, name, kind == "m", ct),
                    "f" => $"-- {name} is a foreign table: its server and options are not scripted here.\n",
                    _ => await TableScriptAsync(conn, policy, obj, name, ct),
                };
            }
            case ObjectKind.Function:
            {
                await using var cmd = Command(conn, """
                    select p.oid, p.prokind::text from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                    where n.nspname = @schema and p.proname = @name and pg_get_function_identity_arguments(p.oid) = @args
                    """, obj.Schema, obj.Name);
                cmd.Parameters.Add(new NpgsqlParameter("args", NpgsqlTypes.NpgsqlDbType.Text) { Value = obj.Arguments ?? "" });
                uint oid;
                string kind;
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await r.ReadAsync(ct)) return null;
                    oid = r.GetFieldValue<uint>(0);
                    kind = r.GetString(1);
                }
                if (!create)
                    return $"DROP {(kind == "p" ? "PROCEDURE" : kind == "a" ? "AGGREGATE" : "FUNCTION")} {name}({obj.Arguments});";
                if (kind == "a") return $"-- {name}({obj.Arguments}) is an aggregate: Postgres cannot print its definition.\n";
                await using var def = new NpgsqlCommand("select pg_get_functiondef(@oid)", conn);
                def.Parameters.Add(new NpgsqlParameter("oid", NpgsqlTypes.NpgsqlDbType.Oid) { Value = oid });
                return ((string)(await def.ExecuteScalarAsync(ct))!).TrimEnd() + ";";
            }
            case ObjectKind.Sequence:
                return create ? await SequenceScriptAsync(conn, obj, name, ct) : $"DROP SEQUENCE {name};";
            case ObjectKind.Type:
                return create ? await TypeScriptAsync(conn, policy, obj, name, ct) : $"DROP TYPE {name};";
        }
        return null;
    }

    private static async Task<string> ViewScriptAsync(NpgsqlConnection conn, IIdentifierPolicy policy, ObjectRef obj, string name, bool materialized, CancellationToken ct)
    {
        await using var cmd = Command(conn, $"select pg_get_viewdef({RelationOid}, true)", obj.Schema, obj.Name);
        var def = ((string)(await cmd.ExecuteScalarAsync(ct))!).Trim().TrimEnd(';');
        var sb = new StringBuilder(materialized
            ? $"CREATE MATERIALIZED VIEW {name} AS\n{def}\nWITH DATA;\n"
            : $"CREATE OR REPLACE VIEW {name} AS\n{def};\n");
        if (materialized) await AppendIndexesAsync(conn, obj, sb, ct);
        await AppendCommentsAsync(conn, policy, obj, name, materialized ? "MATERIALIZED VIEW" : "VIEW", sb, ct);
        return sb.ToString();
    }

    /// <summary>
    /// CREATE TABLE as Postgres would describe it: columns (type, identity or generated, default, not null), constraints
    /// (pg_get_constraintdef), the partition key, then indexes that back no constraint, triggers and comments.
    /// </summary>
    private static async Task<string> TableScriptAsync(NpgsqlConnection conn, IIdentifierPolicy policy, ObjectRef obj, string name, CancellationToken ct)
    {
        var lines = new List<string>();
        await using (var cmd = Command(conn, $"""
            select a.attname::text, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.attidentity::text, a.attgenerated::text,
                   pg_get_expr(d.adbin, d.adrelid),
                   case when a.attcollation <> t.typcollation then (select collname::text from pg_catalog.pg_collation where oid = a.attcollation) end
            from pg_catalog.pg_attribute a
            join pg_catalog.pg_type t on t.oid = a.atttypid
            left join pg_catalog.pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum
            where a.attrelid = {RelationOid} and a.attnum > 0 and not a.attisdropped
            order by a.attnum
            """, obj.Schema, obj.Name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var line = new StringBuilder($"    {policy.Quote(r.GetString(0))} {r.GetString(1)}");
                if (!r.IsDBNull(6)) line.Append($" COLLATE {policy.Quote(r.GetString(6))}");
                var identity = r.GetString(3);
                var generated = r.GetString(4);
                var expr = Str(r, 5);
                if (identity is "a" or "d") line.Append(identity == "a" ? " GENERATED ALWAYS AS IDENTITY" : " GENERATED BY DEFAULT AS IDENTITY");
                else if (generated != "" && expr is not null) line.Append($" GENERATED ALWAYS AS ({expr}) {(generated == "v" ? "VIRTUAL" : "STORED")}");
                else if (expr is not null) line.Append($" DEFAULT {expr}");
                if (r.GetBoolean(2)) line.Append(" NOT NULL");
                lines.Add(line.ToString());
            }
        await using (var cmd = Command(conn, $"""
            select con.conname::text, pg_get_constraintdef(con.oid, true)
            from pg_catalog.pg_constraint con
            where con.conrelid = {RelationOid} and con.contype in ('p', 'u', 'f', 'c', 'x')
            order by case con.contype when 'p' then 0 when 'u' then 1 when 'c' then 2 when 'x' then 3 else 4 end, con.conname
            """, obj.Schema, obj.Name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) lines.Add($"    CONSTRAINT {policy.Quote(r.GetString(0))} {r.GetString(1)}");

        string? partitionKey;
        await using (var cmd = Command(conn, $"select case when c.relkind = 'p' then pg_get_partkeydef(c.oid) end from pg_catalog.pg_class c where c.oid = {RelationOid}", obj.Schema, obj.Name))
            partitionKey = await cmd.ExecuteScalarAsync(ct) as string;

        var sb = new StringBuilder($"CREATE TABLE {name} (\n{string.Join(",\n", lines)}\n){(partitionKey is null ? "" : $" PARTITION BY {partitionKey}")};\n");
        await AppendIndexesAsync(conn, obj, sb, ct);
        await using (var cmd = Command(conn, $"select pg_get_triggerdef(t.oid, true) from pg_catalog.pg_trigger t where t.tgrelid = {RelationOid} and not t.tgisinternal order by t.tgname", obj.Schema, obj.Name))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) sb.Append(r.GetString(0)).Append(";\n");
        await AppendCommentsAsync(conn, policy, obj, name, "TABLE", sb, ct);
        return sb.ToString();
    }

    /// <summary>The indexes no constraint stands for (those that do come with their constraint).</summary>
    private static async Task AppendIndexesAsync(NpgsqlConnection conn, ObjectRef obj, StringBuilder sb, CancellationToken ct)
    {
        await using var cmd = Command(conn, $"""
            select pg_get_indexdef(i.indexrelid) from pg_catalog.pg_index i join pg_catalog.pg_class ic on ic.oid = i.indexrelid
            where i.indrelid = {RelationOid}
              and not exists (select 1 from pg_catalog.pg_constraint con where con.conindid = i.indexrelid and con.conrelid = i.indrelid)
            order by ic.relname
            """, obj.Schema, obj.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) sb.Append(r.GetString(0)).Append(";\n");
    }

    private static async Task AppendCommentsAsync(NpgsqlConnection conn, IIdentifierPolicy policy, ObjectRef obj, string name, string what,
        StringBuilder sb, CancellationToken ct)
    {
        await using var cmd = Command(conn, $"""
            select null::text, obj_description({RelationOid}, 'pg_class')
            union all
            select a.attname::text, col_description(a.attrelid, a.attnum) from pg_catalog.pg_attribute a
            where a.attrelid = {RelationOid} and a.attnum > 0 and not a.attisdropped and col_description(a.attrelid, a.attnum) is not null
            """, obj.Schema, obj.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (r.IsDBNull(1)) continue;
            var text = "'" + r.GetString(1).Replace("'", "''") + "'";
            sb.Append(r.IsDBNull(0) ? $"COMMENT ON {what} {name} IS {text};\n" : $"COMMENT ON COLUMN {name}.{policy.Quote(r.GetString(0))} IS {text};\n");
        }
    }

    private static async Task<string?> SequenceScriptAsync(NpgsqlConnection conn, ObjectRef obj, string name, CancellationToken ct)
    {
        await using var cmd = Command(conn, """
            select format_type(s.seqtypid, null), s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle
            from pg_catalog.pg_sequence s join pg_catalog.pg_class c on c.oid = s.seqrelid
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where n.nspname = @schema and c.relname = @name
            """, obj.Schema, obj.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return $"CREATE SEQUENCE {name} AS {r.GetString(0)} INCREMENT BY {r.GetInt64(2)} MINVALUE {r.GetInt64(3)} MAXVALUE {r.GetInt64(4)} " +
               $"START WITH {r.GetInt64(1)} CACHE {r.GetInt64(5)}{(r.GetBoolean(6) ? " CYCLE" : " NO CYCLE")};\n";
    }

    private static async Task<string?> TypeScriptAsync(NpgsqlConnection conn, IIdentifierPolicy policy, ObjectRef obj, string name, CancellationToken ct)
    {
        await using var cmd = Command(conn, """
            select t.typtype::text,
                   (select array_agg(e.enumlabel::text order by e.enumsortorder) from pg_catalog.pg_enum e where e.enumtypid = t.oid),
                   format_type(t.typbasetype, t.typtypmod),
                   (select string_agg(pg_get_constraintdef(c.oid, true), ' ') from pg_catalog.pg_constraint c where c.contypid = t.oid),
                   (select string_agg(quote_ident(a.attname) || ' ' || format_type(a.atttypid, a.atttypmod), ', ' order by a.attnum)
                      from pg_catalog.pg_attribute a where a.attrelid = t.typrelid and a.attnum > 0 and not a.attisdropped),
                   (select format_type(rg.rngsubtype, null) from pg_catalog.pg_range rg where rg.rngtypid = t.oid),
                   t.typnotnull, pg_get_expr(t.typdefaultbin, 0)
            from pg_catalog.pg_type t join pg_catalog.pg_namespace n on n.oid = t.typnamespace
            where n.nspname = @schema and t.typname = @name
            """, obj.Schema, obj.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return r.GetString(0) switch
        {
            "e" => $"CREATE TYPE {name} AS ENUM ({string.Join(", ", Names(r, 1).Select(v => "'" + v.Replace("'", "''") + "'"))});\n",
            "d" => $"CREATE DOMAIN {name} AS {r.GetString(2)}{(r.IsDBNull(7) ? "" : $" DEFAULT {r.GetString(7)}")}{(r.GetBoolean(6) ? " NOT NULL" : "")}"
                   + $"{(r.IsDBNull(3) ? "" : " " + r.GetString(3))};\n",
            "c" => $"CREATE TYPE {name} AS ({Str(r, 4)});\n",
            "r" => $"CREATE TYPE {name} AS RANGE (SUBTYPE = {Str(r, 5)});\n",
            _ => null,
        };
    }
}
