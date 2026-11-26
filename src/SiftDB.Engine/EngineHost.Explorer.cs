using System.Text.Json;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// The schema explorer (design section 14, F13): one relation's details, a schema's other objects, and "Script as", each
/// asked for when the tree needs it. Read by the provider on a connection of its own; the session is never touched.
/// </summary>
public sealed partial class EngineHost
{
    /// <summary>
    /// The names in <paramref name="names"/> that must be written differently from how they are stored (quoted: mixed
    /// case, a reserved word, a space), so the tree can drag or copy a name exactly as SQL needs it. Names that are written
    /// as stored are left out.
    /// </summary>
    private static Dictionary<string, string> Quoted(IIdentifierPolicy policy, IEnumerable<string> names)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in names)
            if (!map.ContainsKey(n) && policy.Quote(n) is var q && q != n) map[n] = q;
        return map;
    }

    private static IEnumerable<string> NamesIn(DatabaseSchema schema) =>
        schema.Schemas.SelectMany(s => s.Relations.SelectMany(r => r.Columns.Select(c => c.Name).Prepend(r.Name)).Prepend(s.Name));

    private async Task RelationDetailAsync(int id, int connectionId, string schema, string relation)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var d = await state.Connection.DescribeRelationAsync(schema, relation, CancellationToken.None);
        Event(new
        {
            type = "relationDetail", replyTo = id, found = d is not null, detail = d,
            quoted = d is null ? [] : Quoted(state.Provider.Identifiers,
                d.Keys.Select(k => k.Name).Concat(d.ForeignKeys.Select(f => f.Name)).Concat(d.Indexes.Select(i => i.Name))
                    .Concat(d.Checks.Select(c => c.Name)).Concat(d.Triggers.Select(t => t.Name)).Concat(d.Partitions.Select(p => p.Name))),
        });
    }

    private async Task SchemaObjectsAsync(int id, int connectionId, string schema)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var o = await state.Connection.DescribeSchemaObjectsAsync(schema, CancellationToken.None);
        Event(new
        {
            type = "schemaObjects", replyTo = id, found = o is not null, objects = o,
            quoted = o is null ? [] : Quoted(state.Provider.Identifiers,
                o.Functions.Select(f => f.Name).Concat(o.Sequences.Select(s => s.Name)).Concat(o.Types.Select(t => t.Name))),
        });
    }

    /// <summary>
    /// "scriptObject": the statement for "Script as CREATE / DROP / SELECT", put into the sheet, never run. CREATE and DROP
    /// are the provider's (they are the database's own DDL); SELECT is written here from the schema cache and laid out like
    /// Format SQL (F7), with every column named.
    /// </summary>
    private const int TopRows = 100;

    private async Task ScriptObjectAsync(int id, int connectionId, JsonElement root)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var kind = Enum.TryParse<ObjectKind>(OptionalString(root, "kind"), ignoreCase: true, out var k) ? k : ObjectKind.Relation;
        var obj = new ObjectRef(kind, root.GetProperty("schema").GetString()!, root.GetProperty("name").GetString()!, OptionalString(root, "arguments"));
        var what = OptionalString(root, "script") ?? "create";
        string? sql;
        if (what is "select" or "top")
        {
            var rel = state.Schema is { } schema && kind == ObjectKind.Relation ? Relation(schema, obj.Schema, obj.Name) : null;
            if (rel is null) { Error(id, "That table is not in the schema cache. Refresh the schema and try again."); return; }
            var policy = state.Provider.Identifiers;
            var table = (!state.Provider.WritesSchemaNames || obj.Schema == "public" ? "" : policy.Quote(obj.Schema) + ".") + policy.Quote(rel.Name);
            // "Select top 100 rows": a quick look at what a table holds, limited the dialect's way (Postgres: LIMIT).
            // Without such a clause (DynamoDB) the rows still come page by page, as the grid shows them.
            var limit = what == "top" ? state.Provider.RowLimitClause(TopRows) : null;
            var plain = what == "top"
                ? $"select * from {table}{(limit is null ? "" : " " + limit)}"
                : $"select {string.Join(", ", rel.Columns.Select(c => policy.Quote(c.Name)))} from {table}";
            // Format SQL's layout only where the provider can check it keeps the meaning (F7); otherwise one line.
            sql = (state.Provider.SameMeaning(plain, plain) is true
                ? SqlFormatter.Format(plain, state.Provider.Parser.Tokenize(plain), CurrentSettings.Format)
                : plain) + ";";
        }
        else sql = await state.Connection.ScriptAsync(obj, create: what == "create", CancellationToken.None);
        if (sql is null) { Error(id, "This cannot be scripted."); return; }
        Event(new { type = "script", replyTo = id, sql });
    }
}
