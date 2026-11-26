using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Dry runs against a real server (design section 14, F2). Needs SIFTDB_SPIKE_CONN. Creates two uniquely named tables,
/// dropped at the end. Most checks need Postgres 18 or later (before-and-after rows); the dev server and CI run 18.
/// </summary>
public class DryRunTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly ConcurrentQueue<string> _events = new();
        public readonly ConcurrentDictionary<int, List<byte[]>> Batches = new();
        public void SendEvent(string json) => _events.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch b) => Batches.GetOrAdd(queryId, _ => []).Add(b.Data);
        public List<JsonElement> All => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone()).ToList();
        public JsonElement Reply(int id) => All.Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _t = "siftdb_dry_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _u = "siftdb_dryu_" + Guid.NewGuid().ToString("N")[..8];
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request, _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([new PostgresProvider()], _sink);
        _conn = (await Send(new { type = "connect", provider = "postgres", connectionString = _cs })).GetProperty("connectionId").GetInt32();
        await Outside($"create table {_t} (id int primary key, x int); insert into {_t} values (1, 10), (2, 20), (3, 30)");
        await Outside($"create table {_u} (id int, bonus int); insert into {_u} values (1, 5), (2, 7)");
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        await _engine.DisposeAsync();
        try { await Outside($"drop table if exists {_t}, {_u}"); } catch { /* best effort */ }
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task Outside(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> Table()
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"select string_agg(id || '=' || x, ',' order by id) from {_t}", c);
        return (string)(await cmd.ExecuteScalarAsync())!;
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

    /// <summary>Runs a command that streams (execute or dryRun) to the end. Returns its first reply and the "complete" (or "error") event.</summary>
    private async Task<(JsonElement Reply, JsonElement? End)> Stream(string type, string sql)
    {
        var before = StateEvents();
        var r = await Send(new { type, connectionId = _conn, sql, allRowsChecked = true });
        if (r.GetProperty("type").GetString() != "columns") { await Settle(before); return (r, null); }
        var q = r.GetProperty("queryId").GetInt32();
        JsonElement? end = null;
        for (var i = 0; i < 500 && end is null; i++)
        {
            await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId = q }));
            end = _sink.All.Cast<JsonElement?>().FirstOrDefault(e => e!.Value.TryGetProperty("queryId", out var qq) && qq.GetInt32() == q
                && e.Value.GetProperty("type").GetString() is "complete" or "error");
        }
        await Settle(before);
        return (r, end);
    }

    private async Task Settle(int before)
    {
        for (var i = 0; i < 500 && StateEvents() <= before; i++) await Task.Delay(10);
    }

    private string LastState() => _sink.All.Last(e => e.GetProperty("type").GetString() == "transaction").GetProperty("state").GetString()!;

    /// <summary>The Int32 values of one column of a query's first batch (see RowBatchWriter's layout).</summary>
    private List<int> Ints(int queryId, int col)
    {
        var d = _sink.Batches[queryId][0];
        var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12 + col * 20 + 4));
        return Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off + i * 4))).ToList();
    }

    [SkippableFact]
    public async Task An_update_shows_each_row_before_and_after_and_changes_nothing()
    {
        Require();
        var (r, end) = await Stream("dryRun", $"update {_t} set x = x + 1 where id <= 2");
        var dry = r.GetProperty("dryRun");
        Assert.Equal("beforeAndAfter", dry.GetProperty("shape").GetString());
        Assert.Equal(2, dry.GetProperty("width").GetInt32());
        Assert.Equal(["id", "x", "id", "x"], r.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        var q = r.GetProperty("queryId").GetInt32();
        Assert.Equal([10, 20], Ints(q, 1).Order());  // before
        Assert.Equal([11, 21], Ints(q, 3).Order());  // after
        Assert.Equal(2, end!.Value.GetProperty("rowsAffected").GetInt64());
        Assert.Equal("1=10,2=20,3=30", await Table());
        Assert.Equal("none", LastState()); // its own transaction, rolled back
    }

    [SkippableFact]
    public async Task A_delete_shows_the_rows_that_would_go()
    {
        Require();
        var (r, end) = await Stream("dryRun", $"delete from {_t} where id = 3");
        Assert.Equal("deleted", r.GetProperty("dryRun").GetProperty("shape").GetString());
        Assert.Equal([30], Ints(r.GetProperty("queryId").GetInt32(), 1));
        Assert.Equal(1, end!.Value.GetProperty("rowsAffected").GetInt64());
        Assert.Equal("1=10,2=20,3=30", await Table());
    }

    [SkippableFact]
    public async Task The_statements_own_returning_is_replaced_and_a_trailing_comment_is_kept_out_of_the_way()
    {
        Require();
        var (r, _) = await Stream("dryRun", $"update {_t} set x = 0 returning id -- a note\n;");
        Assert.Equal(4, r.GetProperty("columns").GetArrayLength());
        Assert.Equal("1=10,2=20,3=30", await Table());
    }

    [SkippableFact]
    public async Task An_update_from_another_table_returns_only_the_target_tables_columns()
    {
        Require();
        var (r, _) = await Stream("dryRun", $"update {_t} t set x = t.x + u.bonus from {_u} u where u.id = t.id");
        Assert.Equal(4, r.GetProperty("columns").GetArrayLength()); // id, x before; id, x after: none of u's
        Assert.Equal([15, 27], Ints(r.GetProperty("queryId").GetInt32(), 3).Order());
    }

    [SkippableFact]
    public async Task Inside_a_transaction_its_changes_stay_and_the_bar_stays_green()
    {
        Require();
        await Send(new { type = "transaction", connectionId = _conn, action = "begin" });
        await Task.Delay(50);
        await Stream("dryRun", $"delete from {_t}");
        Assert.Equal("open", LastState());          // nothing changed, whatever the transaction id says

        await Stream("execute", $"update {_t} set x = 99 where id = 1");
        Assert.Equal("changed", LastState());
        await Stream("dryRun", $"delete from {_t}");
        Assert.Equal("changed", LastState());       // the earlier change is still pending
        await Send(new { type = "transaction", connectionId = _conn, action = "commit" });
        await Task.Delay(50);
        Assert.Equal("1=99,2=20,3=30", await Table()); // the real update committed; the dry-run delete did not
        await Outside($"update {_t} set x = 10 where id = 1");
    }

    [SkippableFact]
    public async Task A_dry_run_that_fails_leaves_the_transaction_open()
    {
        Require();
        await Send(new { type = "transaction", connectionId = _conn, action = "begin" });
        await Task.Delay(50);
        var (r, end) = await Stream("dryRun", $"update {_t} set x = x / (id - 2)"); // division by zero on row 2
        // It may fail before the first row (the reply) or while streaming (an event): either way it failed.
        Assert.True(r.GetProperty("type").GetString() == "error" || end?.GetProperty("type").GetString() == "error");
        Assert.Equal("open", LastState());
        await Send(new { type = "transaction", connectionId = _conn, action = "rollback" });
    }

    [SkippableFact]
    public async Task Only_the_first_rows_are_sent_but_all_are_counted()
    {
        Require();
        await Stream("execute", $"create temp table dry_many as select g as v from generate_series(1, {EngineHost.DryRunMaxRows + 500}) g");
        var (_, end) = await Stream("dryRun", "update dry_many set v = v + 1");
        Assert.Equal(EngineHost.DryRunMaxRows, end!.Value.GetProperty("totalRows").GetInt64());
        Assert.Equal(EngineHost.DryRunMaxRows + 500, end.Value.GetProperty("rowsAffected").GetInt64());
    }

    [SkippableTheory]
    [InlineData("select 1", "UPDATE and DELETE")]
    [InlineData("insert into t values (1)", "UPDATE and DELETE")]
    [InlineData("update t set x = 1; update t set x = 2", "one UPDATE or DELETE")]
    [InlineData("with gone as (delete from t returning *) update t set x = 1", "WITH clause")]
    [InlineData("update t set", "does not parse")]
    [InlineData("truncate t", "UPDATE and DELETE")]
    public async Task Refused(string sql, string why)
    {
        Require();
        var r = await Send(new { type = "dryRun", connectionId = _conn, sql });
        // It fails now and then, rarely and not on demand (design section 15): on a failure, say the whole reply.
        Assert.True(r.GetProperty("type").GetString() == "error" && r.GetProperty("message").GetString()!.Contains(why),
            $"Expected an error saying \"{why}\"; the engine answered: {r}");
    }
}
