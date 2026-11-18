using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// The missing-WHERE warning against a real server (design section 14, F1). Needs SIFTDB_SPIKE_CONN. Creates one uniquely
/// named table, dropped at the end.
/// </summary>
public class AllRowsWarningTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly ConcurrentQueue<string> _events = new();
        public void SendEvent(string json) => _events.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch b) { }
        public JsonElement Reply(int id) => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
            .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
        public IEnumerable<JsonElement> Of(string type) => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
            .Where(e => e.GetProperty("type").GetString() == type);
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _table = "siftdb_allrows_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"siftdb-settings-{Guid.NewGuid():N}.json");
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request, _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([new PostgresProvider()], _sink, settings: new SettingsStore(_settingsPath));
        _conn = (await Send(new { type = "connect", provider = "postgres", connectionString = _cs })).GetProperty("connectionId").GetInt32();
        await Execute($"create table {_table} (x int)", checkedFirst: true);
        await Execute($"insert into {_table} select generate_series(1, 1500)", checkedFirst: true);
        await Execute($"analyze {_table}", checkedFirst: true); // the estimate comes from the statistics
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        try { await Execute($"drop table if exists {_table}", checkedFirst: true); } catch { /* best effort */ }
        await _engine.DisposeAsync();
        File.Delete(_settingsPath);
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task<JsonElement> Send(object cmd)
    {
        var id = ++_request;
        var json = JsonSerializer.SerializeToNode(cmd)!.AsObject();
        json["id"] = id;
        await _engine!.HandleCommandAsync(json.ToJsonString());
        return _sink.Reply(id);
    }

    private async Task<string?> Check(string sql)
    {
        var r = await Send(new { type = "checkRun", connectionId = _conn, sql });
        Assert.Equal("runCheck", r.GetProperty("type").GetString());
        return r.GetProperty("allRows").GetString();
    }

    /// <summary>Runs a statement to the end. Returns the reply: "columns" when it started, "error" when it did not.</summary>
    private async Task<JsonElement> Execute(string sql, bool checkedFirst)
    {
        var r = await Send(new { type = "execute", connectionId = _conn, sql, allRowsChecked = checkedFirst });
        if (r.GetProperty("type").GetString() != "columns") return r;
        var queryId = r.GetProperty("queryId").GetInt32();
        for (var i = 0; i < 200 && !_sink.Of("complete").Any(e => e.GetProperty("queryId").GetInt32() == queryId)
                                && !_sink.Of("error").Any(e => e.TryGetProperty("queryId", out var q) && q.GetInt32() == queryId); i++)
        {
            await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId }));
            await Task.Delay(5);
        }
        await Task.Delay(50); // the transaction state is read after the last row
        return r;
    }

    private async Task<long> Count()
    {
        await using var conn = new Npgsql.NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"select count(*) from {_table}", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [SkippableFact]
    public async Task A_delete_without_where_is_warned_about_with_the_estimate()
    {
        Require();
        var warning = await Check($"delete from {_table}");
        Assert.Equal($"This DELETE has no WHERE, so it deletes every row in {_table} (about 1,500 rows). No transaction is open, so it cannot be undone.", warning);
    }

    [SkippableFact]
    public async Task With_a_where_there_is_no_warning()
    {
        Require();
        Assert.Null(await Check($"delete from {_table} where x = 1"));
        Assert.Null(await Check($"select * from {_table}"));
    }

    [SkippableFact]
    public async Task A_temp_table_is_not_warned_about()
    {
        Require();
        await Execute("create temp table scratch (x int)", checkedFirst: true);
        Assert.Null(await Check("delete from scratch"));
        Assert.Null(await Check("truncate scratch"));
    }

    [SkippableFact]
    public async Task A_table_that_is_not_there_is_still_warned_about_without_an_estimate()
    {
        Require();
        Assert.Equal("This UPDATE has no WHERE, so it changes every row in nowhere_to_be_found. No transaction is open, so it cannot be undone.",
            await Check("update nowhere_to_be_found set x = 1"));
    }

    [SkippableFact]
    public async Task Unconfirmed_it_is_refused_and_nothing_is_deleted()
    {
        Require();
        var r = await Execute($"delete from {_table}", checkedFirst: false);
        Assert.Equal("error", r.GetProperty("type").GetString());
        Assert.StartsWith("Not run: This DELETE has no WHERE", r.GetProperty("message").GetString());
        Assert.Equal(1500, await Count());
    }

    [SkippableFact]
    public async Task Confirmed_it_runs()
    {
        Require();
        Assert.Equal("columns", (await Execute($"delete from {_table}", checkedFirst: true)).GetProperty("type").GetString());
        Assert.Equal(0, await Count());
    }

    [SkippableFact]
    public async Task Inside_a_transaction_it_says_rollback_can_undo_it()
    {
        Require();
        await Send(new { type = "transaction", connectionId = _conn, action = "begin" });
        await Task.Delay(50);
        Assert.EndsWith("It runs inside the open transaction, so Rollback can still undo it.", await Check($"truncate {_table}"));
        await Send(new { type = "transaction", connectionId = _conn, action = "rollback" });
    }

    [SkippableFact]
    public async Task The_setting_switches_it_off()
    {
        Require();
        var saved = await Send(new { type = "saveSettings", settings = new { warnAllRows = false } });
        Assert.False(saved.GetProperty("settings").GetProperty("warnAllRows").GetBoolean());
        Assert.Null(await Check($"delete from {_table}"));
    }
}
