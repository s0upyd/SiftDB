using System.Text.Json;
using System.Text.Json.Nodes;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Format SQL's guarantee (design section 14, F7): two texts mean the same when libpg_query gives them the same parse tree
/// once every position is taken out. Keyword case, spacing and line breaks are not in the tree; everything that decides
/// what runs (names as the server folds them, literals, operators, the structure) is.
/// </summary>
internal static class PostgresSameMeaning
{
    /// <summary>
    /// The fields that only say where something was in the text: <c>location</c> and every <c>*_location</c>, the extent of
    /// a list (an ARRAY[...]'s brackets, a row comparison's right side) and a statement's length. Taken from libpg_query's
    /// own message definitions (every integer field named like a position), so a new kind of node cannot sneak one past;
    /// <see cref="IsPosition"/> has a test that lists them again.
    /// </summary>
    private static readonly HashSet<string> Extents = ["list_start", "list_end", "rexpr_list_start", "rexpr_list_end", "stmt_len"];

    internal static bool IsPosition(string field) =>
        field == "location" || field.EndsWith("_location", StringComparison.Ordinal) || Extents.Contains(field);

    public static bool Same(string a, string b) => Tree(a) is { } x && Tree(b) is { } y && JsonNode.DeepEquals(x, y);

    private static JsonNode? Tree(string sql)
    {
        var parsed = PgSqlParser.Parser.Parse(sql, new PgSqlParser.ParserOptions());
        if (parsed.Error is not null || parsed.Value is null) return null;
        var node = JsonNode.Parse(parsed.Value.ToString());
        Strip(node);
        return node;
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).Where(IsPosition).ToList()) o.Remove(key);
                foreach (var p in o) Strip(p.Value);
                break;
            case JsonArray a:
                foreach (var item in a) Strip(item);
                break;
        }
    }
}
