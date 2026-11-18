using System.Text.Json;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Dry runs on a Postgres session (design section 14, F2): the statement runs for real behind a savepoint (inside an open
/// transaction) or in a transaction of its own (outside one), returns the rows it touches, and is then rolled back.
///
/// The rows come from a RETURNING clause the dry run puts on the statement in place of any it had. On Postgres 18 and
/// later an UPDATE returns each row before and after (<c>RETURNING WITH (OLD AS …, NEW AS …)</c>), in one statement, so
/// nothing has to be matched up afterwards; earlier servers return the rows after only. A DELETE returns the rows it
/// removes. Only the target table's columns are returned: <c>RETURNING *</c> would add those of an UPDATE's FROM tables.
/// </summary>
internal sealed class PostgresDryRun(NpgsqlConnection conn, PostgresSqlParser parser, PostgresTransactionMonitor monitor) : IDryRunSupport
{
    private const string Savepoint = "siftdb_dryrun";
    private const string OldAlias = "siftdb_old", NewAlias = "siftdb_new";

    /// <summary>How the last BeginAsync started: a savepoint in the user's transaction, or a transaction of its own.</summary>
    private bool _usedSavepoint;

    public DryRunPlan Plan(string sql)
    {
        var parsed = PgSqlParser.Parser.Parse(sql, new PgSqlParser.ParserOptions());
        if (parsed.Error is not null || parsed.Value is null)
            return DryRunPlan.No("This does not parse, so it cannot be dry-run. Fix the syntax error first.");
        using var doc = JsonDocument.Parse(parsed.Value.ToString());
        var stmts = doc.RootElement.TryGetProperty("stmts", out var s) ? s.EnumerateArray().ToList() : [];
        if (stmts.Count != 1) return DryRunPlan.No("A dry run works on one UPDATE or DELETE statement at a time.");
        if (!stmts[0].TryGetProperty("stmt", out var stmt)) return DryRunPlan.No("There is nothing to dry-run.");
        var node = stmt.EnumerateObject().First();
        var update = node.Name == "UpdateStmt";
        if (!update && node.Name != "DeleteStmt")
            return DryRunPlan.No("A dry run works on UPDATE and DELETE statements.");
        var body = node.Value;
        // A CTE that changes data would change it unseen: the rows shown are only the main statement's.
        if (body.TryGetProperty("withClause", out var with) && ChangesData(with))
            return DryRunPlan.No("This statement changes data in its WITH clause too, which a dry run cannot show.");
        if (!body.TryGetProperty("relation", out var relation)) return DryRunPlan.No("There is no table to dry-run.");
        var target = relation.TryGetProperty("alias", out var alias) && Str(alias, "aliasname") is { Length: > 0 } a ? a
            : Str(relation, "relname") ?? "";
        var t = Quote(target);

        var withOld = update && conn.PostgreSqlVersion.Major >= 18;
        var returning = !update ? $"returning {t}.*"
            : withOld ? $"returning with (old as {OldAlias}, new as {NewAlias}) {OldAlias}.*, {NewAlias}.*"
            : $"returning {t}.*";
        var shape = !update ? DryRunShape.Deleted : withOld ? DryRunShape.BeforeAndAfter : DryRunShape.AfterOnly;
        return new DryRunPlan(WithReturning(sql, returning), shape, null);
    }

    /// <summary>
    /// The statement with <paramref name="returning"/> in place of its own RETURNING clause, or added at its end. Found in
    /// the scanner's tokens, so strings and comments are never mistaken for SQL: the statement's own RETURNING is the
    /// keyword outside any parentheses (one inside a CTE is that CTE's). A trailing ';' or comment stays out of the way.
    /// </summary>
    private string WithReturning(string sql, string returning)
    {
        var tokens = parser.Tokenize(sql);
        int depth = 0, own = -1, end = 0;
        foreach (var tok in tokens)
        {
            if (tok.Kind == SqlTokenKind.Comment) continue;
            var text = sql.AsSpan(tok.Start, tok.Length);
            if (text is ";") continue;
            if (text is "(") depth++;
            else if (text is ")") depth--;
            else if (depth == 0 && tok.Kind == SqlTokenKind.Keyword && text.Equals("returning", StringComparison.OrdinalIgnoreCase)) own = tok.Start;
            end = tok.End;
        }
        return own >= 0 ? $"{sql[..own]}{returning}" : $"{sql[..end]} {returning}";
    }

    private static bool ChangesData(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) if (ChangesData(item)) return true;
                return false;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name is "InsertStmt" or "UpdateStmt" or "DeleteStmt" or "MergeStmt") return true;
                    if (ChangesData(p.Value)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    public async Task BeginAsync(CancellationToken ct)
    {
        switch (monitor.ProtocolStatus)
        {
            case "InTransactionBlock":
                await monitor.SendAsync($"savepoint {Savepoint}", ct);
                _usedSavepoint = true;
                break;
            case "Idle":
                await monitor.SendAsync("begin", ct);
                _usedSavepoint = false;
                break;
            default:
                throw new InvalidOperationException("The transaction has failed. Roll back before a dry run.");
        }
    }

    /// <summary>A rollback to the savepoint works in an aborted transaction too, so a statement that failed is undone the same way.</summary>
    public Task EndAsync(CancellationToken ct) => monitor.SendAsync(
        _usedSavepoint ? $"rollback to savepoint {Savepoint}; release savepoint {Savepoint}" : "rollback", ct);

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
