using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Visual EXPLAIN against a real server (design section 14, F6). Needs SIFTDB_SPIKE_CONN. Creates a uniquely named table
/// of 20,000 rows, dropped at the end.
/// </summary>
public class PlanTests : IAsyncLifetime
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
    private readonly string _t = "siftdb_plan_" + Guid.NewGuid().ToString("N")[..8];
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request, _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([new PostgresProvider()], _sink);
        _conn = (await Send(new { type = "connect", provider = "postgres", connectionString = _cs })).GetProperty("connectionId").GetInt32();
        await Outside($"create table {_t} (id int primary key, x int); insert into {_t} select g, g % 100 from generate_series(1, 20000) g; analyze {_t}");
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        await _engine.DisposeAsync();
        try { await Outside($"drop table if exists {_t}"); } catch { /* best effort */ }
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task Outside(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Rows()
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"select count(*) from {_t}", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<JsonElement> Send(object cmd)
    {
        var id = ++_request;
        var json = JsonSerializer.SerializeToNode(cmd)!.AsObject();
        json["id"] = id;
        await _engine!.HandleCommandAsync(json.ToJsonString());
        return _sink.Reply(id);
    }

    private Task<JsonElement> Explain(string sql, bool analyze = false, string? source = null, int sourceStart = 0) =>
        Send(new { type = "explain", connectionId = _conn, sql, analyze, source, sourceStart });

    private static IEnumerable<JsonElement> Nodes(JsonElement n) =>
        new[] { n }.Concat(n.GetProperty("children").EnumerateArray().SelectMany(Nodes));

    private string LastState() => _sink.All.Last(e => e.GetProperty("type").GetString() == "transaction").GetProperty("state").GetString()!;

    [SkippableFact]
    public async Task Estimates_only_give_costs_and_the_text_form()
    {
        Require();
        var r = await Explain($"select * from {_t} where x = 5");
        Assert.Equal("plan", r.GetProperty("type").GetString());
        var plan = r.GetProperty("plan");
        Assert.False(plan.GetProperty("analyzed").GetBoolean());
        Assert.Equal("cost", plan.GetProperty("measure").GetString());
        var root = plan.GetProperty("root");
        Assert.Contains($"on {_t}", root.GetProperty("label").GetString());
        Assert.True(root.GetProperty("value").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("actualRows").ValueKind);
        Assert.Contains("Seq Scan", plan.GetProperty("text").GetString());
        Assert.Contains("Filter: ", plan.GetProperty("text").GetString());
        // 20,000 rows read to keep about 200: worth a remark, even without running it.
        Assert.Contains(Nodes(root), n => n.GetProperty("advice").EnumerateArray().Any(a => a.GetProperty("kind").GetString() == "wholeTable"));
    }

    [SkippableFact]
    public async Task Analyze_times_the_statement_and_says_how_many_rows_it_kept()
    {
        Require();
        var plan = (await Explain($"select * from {_t} where x = 5", analyze: true)).GetProperty("plan");
        Assert.True(plan.GetProperty("analyzed").GetBoolean());
        Assert.Equal("time", plan.GetProperty("measure").GetString());
        Assert.True(plan.GetProperty("executionMs").GetDouble() > 0);
        var scan = Nodes(plan.GetProperty("root")).First(n => n.GetProperty("operation").GetString()!.Contains("Seq Scan"));
        Assert.Equal(200, scan.GetProperty("actualRows").GetDouble() * scan.GetProperty("loops").GetDouble(), 0.5);
        var advice = scan.GetProperty("advice").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "wholeTable");
        Assert.Contains("Reads 20,000 rows", advice.GetProperty("message").GetString());
        Assert.Contains("actual time=", plan.GetProperty("text").GetString());
    }

    [SkippableFact]
    public async Task Analyze_of_a_delete_runs_it_and_rolls_it_back()
    {
        Require();
        var plan = (await Explain($"delete from {_t} where id <= 100", analyze: true)).GetProperty("plan");
        Assert.True(plan.GetProperty("changedData").GetBoolean());
        Assert.Equal("Delete", plan.GetProperty("root").GetProperty("operation").GetString());
        Assert.Equal(20000, await Rows());
        Assert.Equal("none", LastState());
    }

    [SkippableFact]
    public async Task A_failing_explain_inside_a_transaction_leaves_it_usable()
    {
        Require();
        await Send(new { type = "transaction", connectionId = _conn, action = "begin" });
        var bad = await Explain($"select nope from {_t}");
        Assert.Equal("error", bad.GetProperty("type").GetString());
        Assert.Equal("open", LastState());
        Assert.Equal("plan", (await Explain($"select * from {_t}")).GetProperty("type").GetString());
        Assert.Equal("open", LastState());
        await Send(new { type = "transaction", connectionId = _conn, action = "rollback" });
    }

    [SkippableFact]
    public async Task Steps_link_to_where_their_table_is_written()
    {
        Require();
        var source = $"select *\nfrom {_t} t\nwhere t.x = 5";
        var plan = (await Explain(source, source: source, sourceStart: 100)).GetProperty("plan");
        var scan = Nodes(plan.GetProperty("root")).First(n => n.GetProperty("sql").GetArrayLength() > 0);
        var range = scan.GetProperty("sql")[0];
        Assert.Equal(100 + source.IndexOf(_t, StringComparison.Ordinal), range.GetProperty("start").GetInt32());
        Assert.Equal($"{_t} t".Length, range.GetProperty("length").GetInt32());
    }

    [SkippableFact]
    public async Task More_than_one_statement_is_refused()
    {
        Require();
        var r = await Explain("select 1; select 2");
        Assert.Equal("error", r.GetProperty("type").GetString());
        Assert.Contains("one statement", r.GetProperty("message").GetString());
    }
}
