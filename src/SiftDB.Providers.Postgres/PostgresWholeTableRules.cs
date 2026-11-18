using System.Text.Json;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Finds statements that change every row of a table (design section 14, F1): an UPDATE or DELETE with no WHERE
/// (<c>UPDATE ... FROM</c> and <c>DELETE ... USING</c> included), and TRUNCATE. Read from the parse tree, so comments,
/// casing and line breaks make no difference, every statement in a run is seen, and so is a data-changing CTE
/// (<c>with gone as (delete from t returning *) select ...</c>). Function bodies and DO blocks are text, not looked into.
/// </summary>
internal static class PostgresWholeTableRules
{
    public static IReadOnlyList<WholeTableChange> Find(string sql)
    {
        var parsed = PgSqlParser.Parser.Parse(sql, new PgSqlParser.ParserOptions());
        if (parsed.Error is not null || parsed.Value is null) return [];
        using var doc = JsonDocument.Parse(parsed.Value.ToString());
        var found = new List<WholeTableChange>();
        Walk(doc.RootElement, found);
        return found.Distinct().ToList();
    }

    private static void Walk(JsonElement e, List<WholeTableChange> found)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, found);
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "UpdateStmt" when !p.Value.TryGetProperty("whereClause", out _):
                            Add(found, WholeTableAction.Update, p.Value, "relation");
                            break;
                        case "DeleteStmt" when !p.Value.TryGetProperty("whereClause", out _):
                            Add(found, WholeTableAction.Delete, p.Value, "relation");
                            break;
                        case "TruncateStmt" when p.Value.TryGetProperty("relations", out var list) && list.ValueKind == JsonValueKind.Array:
                            foreach (var item in list.EnumerateArray())
                                if (item.TryGetProperty("RangeVar", out var rv)) AddRangeVar(found, WholeTableAction.Truncate, rv);
                            break;
                    }
                    Walk(p.Value, found); // a CTE, or the statements inside a multi-statement run
                }
                break;
        }
    }

    private static void Add(List<WholeTableChange> found, WholeTableAction action, JsonElement stmt, string property)
    {
        if (stmt.TryGetProperty(property, out var rv)) AddRangeVar(found, action, rv);
    }

    private static void AddRangeVar(List<WholeTableChange> found, WholeTableAction action, JsonElement rv)
    {
        if (Str(rv, "relname") is not { Length: > 0 } table) return;
        found.Add(new WholeTableChange(action, Str(rv, "schemaname") is { Length: > 0 } s ? s : null, table));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
