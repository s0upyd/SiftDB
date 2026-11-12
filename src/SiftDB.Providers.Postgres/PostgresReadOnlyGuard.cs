using System.Text.Json;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Spots statements that would take a read-only session out of read-only (design section 8). The server refuses writes
/// by itself; this only stops the user switching that off by accident. It reads the parse tree, so comments, casing and
/// spacing make no difference, and it sees every statement in a multi-statement run. Function bodies and DO blocks are
/// not looked into: a guard against mistakes, not a security boundary.
/// </summary>
internal static class PostgresReadOnlyGuard
{
    private static readonly HashSet<string> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        "default_transaction_read_only", "transaction_read_only",
    };

    // Postgres accepts any unambiguous prefix of these for a boolean.
    private static readonly string[] TrueWords = ["on", "true", "yes", "1"];

    public const string Message =
        "This connection is read-only, and this statement would switch read-only off. To change data, open the sheet on a writable connection.";

    /// <summary>Why <paramref name="sql"/> must not run on a read-only session, or null. SQL that does not parse is left to the server.</summary>
    public static string? Check(string sql)
    {
        var parsed = PgSqlParser.Parser.Parse(sql, new PgSqlParser.ParserOptions());
        if (parsed.Error is not null || parsed.Value is null) return null;
        // The wrapper's messages are protobuf; their JSON form is the simplest stable way to walk every node.
        using var doc = JsonDocument.Parse(parsed.Value.ToString());
        return Walk(doc.RootElement) ? Message : null;
    }

    private static bool Walk(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) if (Walk(item)) return true;
                return false;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name switch
                        {
                            "VariableSetStmt" => IsSwitchOff(p.Value),
                            "TransactionStmt" => HasReadWriteOption(p.Value, "options"),
                            "FuncCall" => IsSetConfig(p.Value),
                            _ => false,
                        }) return true;
                    if (Walk(p.Value)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// SET default_transaction_read_only / transaction_read_only to anything but true, and SET TRANSACTION or SET SESSION
    /// CHARACTERISTICS with READ WRITE. SET ... TO DEFAULT and RESET are fine: the default is the read-only startup value.
    /// </summary>
    private static bool IsSwitchOff(JsonElement set)
    {
        var kind = Str(set, "kind");
        var name = Str(set, "name") ?? "";
        if (kind == "VAR_SET_VALUE" && Settings.Contains(name))
            return !(set.TryGetProperty("args", out var args) && args.GetArrayLength() == 1 && IsTrue(args[0]));
        if (kind == "VAR_SET_MULTI") return HasReadWriteOption(set, "args");
        return false;
    }

    /// <summary>BEGIN / START TRANSACTION / SET TRANSACTION ... READ WRITE: a transaction_read_only option that is not true.</summary>
    private static bool HasReadWriteOption(JsonElement stmt, string listName)
    {
        if (!stmt.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("DefElem", out var def) || !Settings.Contains(Str(def, "defname") ?? "")) continue;
            if (!def.TryGetProperty("arg", out var arg) || !IsTrue(arg)) return true;
        }
        return false;
    }

    /// <summary>set_config('default_transaction_read_only', ...) anywhere, e.g. inside a SELECT. Only a literal name is recognised.</summary>
    private static bool IsSetConfig(JsonElement call)
    {
        if (!call.TryGetProperty("funcname", out var names) || names.GetArrayLength() == 0) return false;
        var last = names[names.GetArrayLength() - 1];
        if (!last.TryGetProperty("String", out var s) || !string.Equals(Str(s, "sval"), "set_config", StringComparison.OrdinalIgnoreCase)) return false;
        if (!call.TryGetProperty("args", out var args) || args.GetArrayLength() == 0) return false;
        return ConstText(args[0]) is { } setting && Settings.Contains(setting);
    }

    /// <summary>An A_Const that is clearly true. Anything else (false, off, 0, a non-literal) counts as not true.</summary>
    private static bool IsTrue(JsonElement node)
    {
        if (!node.TryGetProperty("A_Const", out var c)) return false;
        // Protobuf leaves zero values out: "ival": {} is 0 (READ WRITE), "ival": {"ival": 1} is 1 (READ ONLY).
        if (c.TryGetProperty("ival", out var i)) return i.TryGetProperty("ival", out var n) && n.GetInt64() != 0;
        if (c.TryGetProperty("boolval", out var b)) return b.TryGetProperty("boolval", out var v) && v.GetBoolean();
        if (ConstText(node) is { } text)
        {
            var t = text.Trim().ToLowerInvariant();
            return t.Length > 0 && TrueWords.Any(w => w.StartsWith(t, StringComparison.Ordinal)) && t != "o"; // "o" is ambiguous (on/off)
        }
        return false;
    }

    private static string? ConstText(JsonElement node) =>
        node.TryGetProperty("A_Const", out var c) && c.TryGetProperty("sval", out var s) ? Str(s, "sval") : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
