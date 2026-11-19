using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Query plans on a Postgres session (design section 14, F6): <c>EXPLAIN (FORMAT JSON, VERBOSE[, BUFFERS, ANALYZE])</c>,
/// turned into provider-neutral <see cref="PlanNode"/>s, plus the plan in Postgres's usual text form for copying. The
/// sizes of the tables the plan reads come from <c>pg_class.reltuples</c>, so a whole-table scan of a big table can be
/// told from one of a small table.
/// </summary>
internal sealed class PostgresPlans(NpgsqlConnection conn) : IPlanSupport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<QueryPlan> ExplainAsync(string sql, bool analyze, CancellationToken ct)
    {
        // BUFFERS without ANALYZE (planning buffers) needs Postgres 13.
        var options = analyze ? "FORMAT JSON, VERBOSE, BUFFERS, ANALYZE, TIMING"
            : conn.PostgreSqlVersion.Major >= 13 ? "FORMAT JSON, VERBOSE, BUFFERS" : "FORMAT JSON, VERBOSE";
        string json;
        await using (var cmd = new NpgsqlCommand($"EXPLAIN ({options}) {sql}", conn))
            json = Convert.ToString(await cmd.ExecuteScalarAsync(ct), Inv) ?? "[]";

        using var doc = JsonDocument.Parse(json);
        var top = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
        var relations = new HashSet<(string Schema, string Name)>();
        CollectRelations(top.GetProperty("Plan"), relations);
        var sizes = await TableRowsAsync(relations, ct);
        var root = Node(top.GetProperty("Plan"), analyze, sizes);
        var planning = Number(top, "Planning Time");
        var execution = Number(top, "Execution Time");
        return new QueryPlan(root, analyze, planning, execution, PlanText.Render(top, analyze));
    }

    private static void CollectRelations(JsonElement plan, HashSet<(string, string)> into)
    {
        if (Str(plan, "Relation Name") is { } rel) into.Add((Str(plan, "Schema") ?? "public", rel));
        if (plan.TryGetProperty("Plans", out var kids))
            foreach (var k in kids.EnumerateArray()) CollectRelations(k, into);
    }

    /// <summary>The planner's row estimate for each table; a table never analysed (reltuples -1) has none.</summary>
    private async Task<Dictionary<(string, string), double>> TableRowsAsync(HashSet<(string Schema, string Name)> relations, CancellationToken ct)
    {
        var sizes = new Dictionary<(string, string), double>();
        if (relations.Count == 0) return sizes;
        await using var cmd = new NpgsqlCommand(
            "select n.nspname, c.relname, c.reltuples::float8 from pg_class c join pg_namespace n on n.oid = c.relnamespace " +
            "join unnest(@s::text[], @r::text[]) w(s, r) on w.s = n.nspname and w.r = c.relname", conn);
        cmd.Parameters.AddWithValue("s", relations.Select(r => r.Schema).ToArray());
        cmd.Parameters.AddWithValue("r", relations.Select(r => r.Name).ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (r.GetDouble(2) >= 0) sizes[(r.GetString(0), r.GetString(1))] = r.GetDouble(2);
        return sizes;
    }

    private static PlanNode Node(JsonElement p, bool analyze, Dictionary<(string, string), double> sizes)
    {
        var type = Str(p, "Node Type") ?? "?";
        var operation = Operation(p, type);
        var relation = Str(p, "Relation Name");
        var schema = Str(p, "Schema");
        var alias = Str(p, "Alias");
        var index = Str(p, "Index Name");
        var target = relation ?? Str(p, "CTE Name") ?? Str(p, "Function Name") ?? Str(p, "Tuplestore Name") ?? Str(p, "Table Function Name")
            ?? (type == "Subquery Scan" ? alias : null);
        alias ??= target;

        var label = new StringBuilder(operation);
        if (index is not null) label.Append(" using ").Append(index);
        if (target is not null)
        {
            label.Append(" on ").Append(target);
            if (alias is not null && alias != target) label.Append(' ').Append(alias);
        }
        if (Str(p, "Subplan Name") is { } sub) label.Insert(0, $"{sub}: ");

        double? tableRows = relation is not null && sizes.TryGetValue((schema ?? "public", relation), out var n) ? n : null;
        var children = p.TryGetProperty("Plans", out var kids)
            ? kids.EnumerateArray().Select(k => Node(k, analyze, sizes)).ToList()
            : [];
        return new PlanNode(
            operation, label.ToString(), schema, relation, alias, index,
            EstimatedRows: Number(p, "Plan Rows") ?? 0,
            ActualRows: analyze ? Number(p, "Actual Rows") : null,
            Loops: analyze ? Number(p, "Actual Loops") : null,
            StartupCost: Number(p, "Startup Cost") ?? 0,
            TotalCost: Number(p, "Total Cost") ?? 0,
            ActualStartupMs: analyze ? Number(p, "Actual Startup Time") : null,
            ActualTotalMs: analyze ? Number(p, "Actual Total Time") : null,
            Filter: Str(p, "Filter"),
            Condition: Str(p, "Index Cond") ?? Str(p, "Hash Cond") ?? Str(p, "Merge Cond") ?? Str(p, "Join Filter") ?? Str(p, "Recheck Cond"),
            RowsRemovedByFilter: Number(p, "Rows Removed by Filter"),
            TableRows: tableRows,
            ScansWholeTable: type == "Seq Scan",
            IsNestedLoop: type == "Nested Loop",
            Spill: Spill(p),
            ChangesData: type == "ModifyTable",
            Properties: Properties(p),
            Children: children);
    }

    /// <summary>The operation as Postgres's text form names it: the node type, refined by its strategy or join type.</summary>
    internal static string Operation(JsonElement p, string type)
    {
        var op = type switch
        {
            "Aggregate" => Str(p, "Strategy") switch
            {
                "Hashed" => "HashAggregate", "Sorted" => "GroupAggregate", "Mixed" => "MixedAggregate", _ => "Aggregate",
            },
            "ModifyTable" => Str(p, "Operation") ?? type,
            "SetOp" => Str(p, "Strategy") == "Hashed" ? "HashSetOp" : "SetOp",
            _ => type,
        };
        if (Str(p, "Join Type") is { } join && join != "Inner" && type is "Nested Loop" or "Hash Join" or "Merge Join")
            op = type == "Nested Loop" ? $"Nested Loop {join} Join" : $"{type[..^5]} {join} Join";
        if (Str(p, "Scan Direction") == "Backward") op += " Backward";
        if (p.TryGetProperty("Parallel Aware", out var par) && par.ValueKind == JsonValueKind.True) op = "Parallel " + op;
        return op;
    }

    /// <summary>Why the step used disk, for a person; null when it did not.</summary>
    private static string? Spill(JsonElement p)
    {
        if (Str(p, "Sort Space Type") == "Disk")
            return $"The sort did not fit in memory and used {Kb(Number(p, "Sort Space Used"))} on disk ({Str(p, "Sort Method")}). Raising work_mem may keep it in memory.";
        if (Number(p, "Hash Batches") is { } batches && batches > 1)
            return $"The hash table did not fit in memory and was split into {batches.ToString("N0", Inv)} batches on disk. Raising work_mem may keep it in memory.";
        if (Number(p, "Disk Usage") is { } disk && disk > 0)
            return $"The hash aggregate did not fit in memory and used {Kb(disk)} on disk. Raising work_mem may keep it in memory.";
        if (Number(p, "Temp Written Blocks") is { } blocks && blocks > 0)
            return $"It wrote {(blocks * 8).ToString("N0", Inv)} kB of temporary files.";
        return null;
    }

    private static string Kb(double? kb) => kb is { } v ? $"{v.ToString("N0", Inv)} kB" : "some space";

    private static IReadOnlyList<PlanProperty> Properties(JsonElement p)
    {
        var list = new List<PlanProperty>();
        foreach (var prop in p.EnumerateObject())
        {
            if (prop.Name is "Plans" or "Workers") continue;
            if (Value(prop.Value) is { } v) list.Add(new PlanProperty(prop.Name, v));
        }
        return list;
    }

    internal static string? Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetDouble().ToString(Inv),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Value).Where(x => x is not null)),
        _ => null,
    };

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>
/// A JSON plan written out the way Postgres's own text form reads (<c>EXPLAIN (VERBOSE)</c>), for "Copy plan as text": the
/// node lines with their costs and actuals, then the details people look for (output, conditions, rows removed, sort and
/// hash memory, buffers), then the planning and execution times.
/// </summary>
internal static class PlanText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] Details =
        ["Output", "Index Cond", "Recheck Cond", "Join Filter", "Hash Cond", "Merge Cond", "Filter", "Sort Key", "Group Key"];

    public static string Render(JsonElement top, bool analyze)
    {
        var sb = new StringBuilder();
        Node(sb, top.GetProperty("Plan"), 0, analyze);
        if (PostgresPlans.Number(top, "Planning Time") is { } plan) sb.Append($"Planning Time: {plan.ToString("0.000", Inv)} ms\n");
        if (PostgresPlans.Number(top, "Execution Time") is { } exec) sb.Append($"Execution Time: {exec.ToString("0.000", Inv)} ms\n");
        return sb.ToString().TrimEnd('\n');
    }

    private static void Node(StringBuilder sb, JsonElement p, int level, bool analyze)
    {
        var type = PostgresPlans.Str(p, "Node Type") ?? "?";
        if (PostgresPlans.Str(p, "Subplan Name") is { } sub)
            sb.Append(new string(' ', Math.Max(0, 6 * level - 4))).Append(sub).Append('\n');
        var arrow = level == 0 ? "" : new string(' ', 6 * (level - 1) + 2) + "->  ";
        var detail = new string(' ', 6 * level + 2);

        sb.Append(arrow).Append(PostgresPlans.Operation(p, type));
        if (PostgresPlans.Str(p, "Index Name") is { } index) sb.Append(" using ").Append(index);
        var relation = PostgresPlans.Str(p, "Relation Name");
        var target = relation ?? PostgresPlans.Str(p, "CTE Name") ?? PostgresPlans.Str(p, "Function Name");
        if (target is not null)
        {
            sb.Append(" on ");
            if (relation is not null && PostgresPlans.Str(p, "Schema") is { } schema) sb.Append(schema).Append('.');
            sb.Append(target);
            if (PostgresPlans.Str(p, "Alias") is { } alias && alias != target) sb.Append(' ').Append(alias);
        }
        sb.Append("  (cost=").Append(F2(p, "Startup Cost")).Append("..").Append(F2(p, "Total Cost"))
          .Append(" rows=").Append(Rows(PostgresPlans.Number(p, "Plan Rows")))
          .Append(" width=").Append(Rows(PostgresPlans.Number(p, "Plan Width"))).Append(')');
        if (analyze)
        {
            if (PostgresPlans.Number(p, "Actual Loops") is 0) sb.Append(" (never executed)");
            else sb.Append(" (actual time=").Append(F3(p, "Actual Startup Time")).Append("..").Append(F3(p, "Actual Total Time"))
                   .Append(" rows=").Append(Rows(PostgresPlans.Number(p, "Actual Rows")))
                   .Append(" loops=").Append(Rows(PostgresPlans.Number(p, "Actual Loops"))).Append(')');
        }
        sb.Append('\n');

        foreach (var name in Details)
            if (p.TryGetProperty(name, out var v) && PostgresPlans.Value(v) is { Length: > 0 } text)
            {
                sb.Append(detail).Append(name).Append(": ").Append(text).Append('\n');
                if (name == "Filter" && PostgresPlans.Number(p, "Rows Removed by Filter") is { } removed)
                    sb.Append(detail).Append("Rows Removed by Filter: ").Append(Rows(removed)).Append('\n');
                if (name == "Join Filter" && PostgresPlans.Number(p, "Rows Removed by Join Filter") is { } jr)
                    sb.Append(detail).Append("Rows Removed by Join Filter: ").Append(Rows(jr)).Append('\n');
            }
        if (PostgresPlans.Str(p, "Sort Method") is { } method)
            sb.Append(detail).Append("Sort Method: ").Append(method).Append("  ")
              .Append(PostgresPlans.Str(p, "Sort Space Type") == "Disk" ? "Disk: " : "Memory: ")
              .Append(Rows(PostgresPlans.Number(p, "Sort Space Used"))).Append("kB\n");
        if (PostgresPlans.Number(p, "Hash Buckets") is { } buckets)
            sb.Append(detail).Append("Buckets: ").Append(Rows(buckets)).Append("  Batches: ").Append(Rows(PostgresPlans.Number(p, "Hash Batches")))
              .Append("  Memory Usage: ").Append(Rows(PostgresPlans.Number(p, "Peak Memory Usage"))).Append("kB\n");
        if (Buffers(p) is { } buffers) sb.Append(detail).Append("Buffers: ").Append(buffers).Append('\n');

        if (p.TryGetProperty("Plans", out var kids))
            foreach (var k in kids.EnumerateArray()) Node(sb, k, level + 1, analyze);
    }

    /// <summary>"shared hit=4 read=2, temp written=10", leaving out what is zero; null when all of it is.</summary>
    private static string? Buffers(JsonElement p)
    {
        var groups = new List<string>();
        foreach (var (kind, prefix) in new[] { ("shared", "Shared"), ("local", "Local"), ("temp", "Temp") })
        {
            var parts = new List<string>();
            foreach (var (word, name) in new[] { ("hit", "Hit"), ("read", "Read"), ("dirtied", "Dirtied"), ("written", "Written") })
                if (PostgresPlans.Number(p, $"{prefix} {name} Blocks") is > 0 and var n) parts.Add($"{word}={Rows(n)}");
            if (parts.Count > 0) groups.Add($"{kind} {string.Join(' ', parts)}");
        }
        return groups.Count > 0 ? string.Join(", ", groups) : null;
    }

    private static string F2(JsonElement p, string name) => (PostgresPlans.Number(p, name) ?? 0).ToString("0.00", Inv);
    private static string F3(JsonElement p, string name) => (PostgresPlans.Number(p, name) ?? 0).ToString("0.000", Inv);

    /// <summary>Whole numbers as they are; Postgres 18 reports average rows per loop with two decimals.</summary>
    private static string Rows(double? v) => v is not { } x ? "0" : x == Math.Floor(x) ? x.ToString("0", Inv) : x.ToString("0.00", Inv);
}
