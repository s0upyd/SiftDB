using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Following foreign keys against a real server (design section 14, F4). Needs SIFTDB_SPIKE_CONN. Creates a uniquely
/// named schema with a parent and a child table, dropped at the end.
/// </summary>
public class RowLinksTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly ConcurrentQueue<string> _events = new();
        public void SendEvent(string json) => _events.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch b) { }
        public List<JsonElement> All => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone()).ToList();
        public JsonElement Reply(int id) => All.Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _s = "siftdb_links_" + Guid.NewGuid().ToString("N")[..8];
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request, _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        await Outside($"""
            create schema {_s};
            create table {_s}."Customer" ("Id" int primary key, name text, email text unique);
            create table {_s}.orders (id int primary key, customer_id int references {_s}."Customer", note text);
            create table {_s}.invoices (id int primary key, customer_email text references {_s}."Customer" (email));
            insert into {_s}."Customer" values (1, 'Ada', 'ada@example.com'), (2, 'Bob', 'bob@example.com');
            insert into {_s}.invoices values (1, 'ada@example.com'), (2, 'ada@example.com');
            insert into {_s}.orders select g, 1, 'x' from generate_series(1, 1005) g;
            insert into {_s}.orders values (2000, 2, 'y');
            """);
        _engine = new EngineHost([new PostgresProvider()], _sink);
        _conn = (await Send(new { type = "connect", provider = "postgres", connectionString = _cs })).GetProperty("connectionId").GetInt32();
        await Send(new { type = "introspect", connectionId = _conn, send = false }); // the tables are known from here
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        await _engine.DisposeAsync();
        try { await Outside($"drop schema if exists {_s} cascade"); } catch { /* best effort */ }
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task Outside(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<JsonElement> Send(object cmd)
    {
        var id = ++_request;
        var json = JsonSerializer.SerializeToNode(cmd)!.AsObject();
        json["id"] = id;
        await _engine!.HandleCommandAsync(json.ToJsonString());
        return _sink.Reply(id);
    }

    private int StateEvents() => _sink.All.Count(e => e.GetProperty("type").GetString() is "transaction" or "transactionUnknown");

    /// <summary>Runs a statement to the end, as the UI does. Returns its "columns" reply (or the error).</summary>
    private async Task<JsonElement> Run(string sql)
    {
        var before = StateEvents();
        var r = await Send(new { type = "execute", connectionId = _conn, sql, allRowsChecked = true });
        if (r.GetProperty("type").GetString() == "columns")
        {
            var q = r.GetProperty("queryId").GetInt32();
            for (var i = 0; i < 100 && !_sink.All.Any(e => e.TryGetProperty("queryId", out var x) && x.GetInt32() == q && e.GetProperty("type").GetString() is "complete" or "error"); i++)
                await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId = q }));
        }
        for (var i = 0; i < 300 && StateEvents() <= before; i++) await Task.Delay(10);
        return r;
    }

    private string LastState() => _sink.All.Last(e => e.GetProperty("type").GetString() == "transaction").GetProperty("state").GetString()!;

    [SkippableFact]
    public async Task A_foreign_key_column_is_a_link_and_a_referenced_table_has_a_key()
    {
        Require();
        var r = await Run($"select o.note, o.customer_id, 1 + 1 as two from {_s}.orders o limit 1");
        var cols = r.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal("customer_id", cols[1].GetProperty("origin").GetProperty("column").GetString());
        Assert.Equal(JsonValueKind.Null, cols[2].GetProperty("origin").ValueKind); // computed: no origin
        var link = Assert.Single(r.GetProperty("links").EnumerateArray());
        Assert.Equal([1], link.GetProperty("columns").EnumerateArray().Select(c => c.GetInt32()));
        Assert.Equal("Customer", link.GetProperty("table").GetString());
        Assert.Equal(["Id"], link.GetProperty("targetColumns").EnumerateArray().Select(c => c.GetString()));
        Assert.Empty(r.GetProperty("keys").EnumerateArray()); // orders.id is not in the result, and nothing references orders

        // Both keys of Customer are referenced: its primary key (by orders) and its unique email (by invoices).
        var c = await Run($"select * from {_s}.\"Customer\"");
        var keys = c.GetProperty("keys").EnumerateArray().ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal([0], keys[0].GetProperty("columns").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(keys[0].GetProperty("primary").GetBoolean());
        Assert.Equal(["email"], keys[1].GetProperty("keyColumns").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal([2], keys[1].GetProperty("columns").EnumerateArray().Select(x => x.GetInt32()));
        Assert.False(keys[1].GetProperty("primary").GetBoolean());

        // Without the email column in the result, only the primary key can be asked.
        var idOnly = await Run($"select \"Id\", name from {_s}.\"Customer\"");
        Assert.Single(idOnly.GetProperty("keys").EnumerateArray());
    }

    [SkippableFact]
    public async Task Referenced_by_works_through_a_unique_column_too()
    {
        Require();
        var r = await Send(new { type = "referencedBy", connectionId = _conn, schema = _s, table = "Customer", columns = new[] { "email" }, values = new[] { "ada@example.com" } });
        var only = Assert.Single(r.GetProperty("references").EnumerateArray()); // invoices only: orders points at the primary key
        Assert.Equal("invoices", only.GetProperty("table").GetString());
        Assert.Equal(2, only.GetProperty("count").GetInt64());
        Assert.Equal($"select * from {_s}.invoices where customer_email = 'ada@example.com'", only.GetProperty("sql").GetString());
    }

    [SkippableFact]
    public async Task The_peek_reads_the_sessions_own_uncommitted_rows_and_a_bad_value_leaves_the_transaction_alone()
    {
        Require();
        await Send(new { type = "transaction", connectionId = _conn, action = "begin" });
        await Task.Delay(50);
        await Run($"insert into {_s}.\"Customer\" values (3, 'Cy')");
        var peek = await Send(new { type = "peekRow", connectionId = _conn, schema = _s, table = "Customer", columns = new[] { "Id" }, values = new[] { "3" } });
        Assert.True(peek.GetProperty("found").GetBoolean());
        Assert.False(peek.GetProperty("committedOnly").GetBoolean());
        Assert.Equal(["3", "Cy", null], peek.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()));

        var bad = await Send(new { type = "peekRow", connectionId = _conn, schema = _s, table = "Customer", columns = new[] { "Id" }, values = new[] { "not a number" } });
        Assert.Equal("error", bad.GetProperty("type").GetString());
        await Run("select 1");
        Assert.Equal("changed", LastState()); // the insert is still pending: the failed lookup was undone on its own
        await Send(new { type = "transaction", connectionId = _conn, action = "rollback" });
    }

    [SkippableFact]
    public async Task A_missing_row_is_not_found()
    {
        Require();
        var peek = await Send(new { type = "peekRow", connectionId = _conn, schema = _s, table = "Customer", columns = new[] { "Id" }, values = new[] { "99" } });
        Assert.False(peek.GetProperty("found").GetBoolean());
    }

    [SkippableFact]
    public async Task Referenced_by_counts_to_a_thousand_and_says_how_to_open_them()
    {
        Require();
        var r = await Send(new { type = "referencedBy", connectionId = _conn, schema = _s, table = "Customer", columns = new[] { "Id" }, values = new[] { "1" } });
        var only = Assert.Single(r.GetProperty("references").EnumerateArray());
        Assert.Equal("orders", only.GetProperty("table").GetString());
        Assert.Equal(1000, only.GetProperty("count").GetInt64());
        Assert.True(only.GetProperty("capped").GetBoolean());
        Assert.Equal($"select * from {_s}.orders where customer_id = 1", only.GetProperty("sql").GetString());

        var two = await Send(new { type = "referencedBy", connectionId = _conn, schema = _s, table = "Customer", columns = new[] { "Id" }, values = new[] { "2" } });
        var one = Assert.Single(two.GetProperty("references").EnumerateArray());
        Assert.Equal(1, one.GetProperty("count").GetInt64());
        Assert.False(one.GetProperty("capped").GetBoolean());
    }

    [Theory]
    [InlineData("public", "Customer", "Id", "42", "select * from \"Customer\" where \"Id\" = 42")]
    [InlineData("sales", "orders", "code", "O'Brien", "select * from sales.orders where code = 'O''Brien'")]
    [InlineData("public", "t", "n", "-1.5", "select * from t where n = -1.5")]
    [InlineData("public", "t", "n", "1e5", "select * from t where n = '1e5'")]
    [InlineData("public", "t", "u", "0f8fad5b-d9cb-469f-a165-70867728950e", "select * from t where u = '0f8fad5b-d9cb-469f-a165-70867728950e'")]
    public void The_statement_that_opens_a_row(string schema, string table, string column, string value, string expected) =>
        Assert.Equal(expected, EngineHost.RowQuerySql(new PostgresProvider().Identifiers, new RowKey(schema, table, [column], [value])));

    /// <summary>The row description is read from an internal Npgsql field. If an upgrade moves it, results quietly lose their links: this says why.</summary>
    [Fact]
    public void Npgsql_still_offers_the_row_description() =>
        Assert.True(PostgresRowDescription.Available,
            "PostgresRowDescription can no longer find Npgsql's RowDescription (table oid and column number of each result column). " +
            "Find where this Npgsql version keeps it and update PostgresRowDescription.");
}
