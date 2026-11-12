using System.Text.Json;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Which statements Postgres refuses to run inside a transaction block ("... cannot run inside a transaction block").
/// Auto-transaction keeps a transaction open on the sheet (design section 7), so it has to step out of it for these.
/// Read from the parse tree, so comments and casing make no difference and every statement in a run is seen.
/// </summary>
internal static class PostgresTransactionRules
{
    /// <summary>Refused inside a block whatever their options.</summary>
    private static readonly HashSet<string> Always =
    [
        "VacuumStmt", "CreatedbStmt", "DropdbStmt", "AlterSystemStmt", "CreateTableSpaceStmt", "DropTableSpaceStmt",
        "CreateSubscriptionStmt", "DropSubscriptionStmt",
    ];

    public static bool CannotRunInTransaction(string sql) => AnyStatement(sql, Refuses);

    /// <summary>
    /// True when any statement in <paramref name="sql"/> is transaction control: BEGIN, START TRANSACTION, COMMIT, END,
    /// ROLLBACK, ABORT, SAVEPOINT, RELEASE, ROLLBACK TO, PREPARE TRANSACTION, COMMIT/ROLLBACK PREPARED. SQL that does not
    /// parse is false.
    /// </summary>
    public static bool ControlsTransaction(string sql) => AnyStatement(sql, (type, _) => type == "TransactionStmt");

    private static bool AnyStatement(string sql, Func<string, JsonElement, bool> test)
    {
        var parsed = PgSqlParser.Parser.Parse(sql, new PgSqlParser.ParserOptions());
        if (parsed.Error is not null || parsed.Value is null) return false;
        using var doc = JsonDocument.Parse(parsed.Value.ToString());
        if (!doc.RootElement.TryGetProperty("stmts", out var stmts)) return false;
        foreach (var raw in stmts.EnumerateArray())
        {
            if (!raw.TryGetProperty("stmt", out var stmt)) continue;
            var node = stmt.EnumerateObject().FirstOrDefault();
            if (node.Value.ValueKind == JsonValueKind.Object && test(node.Name, node.Value)) return true;
        }
        return false;
    }

    private static bool Refuses(string type, JsonElement body) => type switch
    {
        _ when Always.Contains(type) => true,
        // CREATE INDEX CONCURRENTLY, DROP INDEX CONCURRENTLY
        "IndexStmt" or "DropStmt" => body.TryGetProperty("concurrent", out var c) && c.ValueKind == JsonValueKind.True,
        // REINDEX ... CONCURRENTLY, and REINDEX DATABASE / SYSTEM
        "ReindexStmt" => HasOption(body, "params", "concurrently")
            || Str(body, "kind") is "REINDEX_OBJECT_DATABASE" or "REINDEX_OBJECT_SYSTEM",
        // CLUSTER with no table reclusters every table, which Postgres only does outside a block
        "ClusterStmt" => !body.TryGetProperty("relation", out _),
        // DISCARD ALL (DISCARD TEMP, PLANS and SEQUENCES are fine)
        "DiscardStmt" => Str(body, "target") == "DISCARD_ALL",
        _ => false,
    };

    private static bool HasOption(JsonElement body, string listName, string option)
    {
        if (!body.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in list.EnumerateArray())
            if (item.TryGetProperty("DefElem", out var def) && string.Equals(Str(def, "defname"), option, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
