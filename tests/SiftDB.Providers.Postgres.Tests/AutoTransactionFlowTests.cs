using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Auto-transaction against the real server, through the engine (design section 7): the sheet is always inside a
/// transaction. Needs SIFTDB_SPIKE_CONN. Writes only to a temp table, which lives and dies with the session.
/// </summary>
public sealed class AutoTransactionFlowTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        public readonly ConcurrentQueue<JsonElement> Events = new();
        public void SendEvent(string json) => Events.Enqueue(JsonDocument.Parse(json).RootElement.Clone());
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string id) => _d.GetValueOrDefault(id);
        public void Set(string id, string secret) => _d[id] = secret;
        public void Remove(string id) => _d.Remove(id);
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly Sink _sink = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-auto-");
    private readonly PostgresProvider _pg = new();
    private EngineHost? _engine;
    private int _nextId;
    private int _connectionId;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([_pg], _sink, new ConnectionStore(Path.Combine(_dir.FullName, "connections.json"), new MemorySecrets()));
        _connectionId = await Open(readOnly: false);
        // The sheet is inside a transaction from the start, so even this DDL waits for Commit.
        await Run("create temp table auto_probe (x int)");
        await Run("insert into auto_probe values (1), (2), (3)");
        await Commit();
    }

    public async Task DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task<int> Open(bool readOnly)
    {
        var parsed = new PostgresProvider().ParseConnectionString(_cs!);
        var saved = await Send(new
        {
            type = "saveConnection", fromConnectionString = _cs,
            connection = new { name = $"auto {++_nextId}", provider = "postgres", fields = parsed.Fields, rememberPassword = true, readOnly, autoTransaction = true },
        });
        var connected = await Send(new { type = "connect", savedId = saved.GetProperty("savedId").GetString() });
        Assert.Equal(!readOnly, connected.GetProperty("autoTransaction").GetBoolean());
        return connected.GetProperty("connectionId").GetInt32();
    }

    private async Task<JsonElement> Send(object command)
    {
        var id = ++_nextId;
        var json = JsonSerializer.SerializeToNode(command)!.AsObject();
        json["id"] = id;
        await _engine!.HandleCommandAsync(json.ToJsonString());
        return _sink.Events.Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private int TransactionEvents(int connectionId) =>
        _sink.Events.Count(e => e.GetProperty("type").GetString() == "transaction" && e.GetProperty("connectionId").GetInt32() == connectionId);

    /// <summary>Runs a statement to the end, as the UI does, and returns the transaction state reported after it.</summary>
    private async Task<JsonElement> Run(string sql, int? connectionId = null)
    {
        var conn = connectionId ?? _connectionId;
        var before = TransactionEvents(conn);
        var reply = await Send(new { type = "execute", connectionId = conn, sql });
        if (reply.GetProperty("type").GetString() == "columns")
        {
            var queryId = reply.GetProperty("queryId").GetInt32();
            for (var i = 0; i < 100 && !_sink.Events.Any(e => e.TryGetProperty("queryId", out var q) && q.GetInt32() == queryId &&
                     e.GetProperty("type").GetString() is "complete" or "error" or "cancelled"); i++)
                await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId }));
        }
        for (var i = 0; i < 1500 && TransactionEvents(conn) <= before; i++) await Task.Delay(10);
        return _sink.Events.Last(e => e.GetProperty("type").GetString() == "transaction" && e.GetProperty("connectionId").GetInt32() == conn);
    }

    private static string State(JsonElement tx) => tx.GetProperty("state").GetString()!;
    private static bool Automatic(JsonElement tx) => tx.GetProperty("automatic").GetBoolean();

    private JsonElement LastTx(int? connectionId = null)
    {
        var conn = connectionId ?? _connectionId;
        return _sink.Events.Last(e => e.GetProperty("type").GetString() == "transaction" && e.GetProperty("connectionId").GetInt32() == conn);
    }

    private async Task<JsonElement> Bar(string action)
    {
        await Send(new { type = "transaction", connectionId = _connectionId, action });
        return LastTx();
    }

    private Task<JsonElement> Commit() => Bar("commit");
    private Task<JsonElement> Rollback() => Bar("rollback");

    [SkippableFact]
    public async Task Connecting_opens_a_transaction_straight_away()
    {
        Require();
        var fresh = await Open(readOnly: false);
        var tx = LastTx(fresh);
        Assert.Equal("open", State(tx));
        Assert.True(Automatic(tx));
        Assert.Equal(0, tx.GetProperty("statements").GetInt32());
    }

    [SkippableFact]
    public async Task Reads_run_inside_it_and_change_nothing()
    {
        Require();
        var tx = await Run("select count(*) from auto_probe");
        Assert.Equal("open", State(tx));
        Assert.True(Automatic(tx));
        Assert.Equal(1, tx.GetProperty("statements").GetInt32());
    }

    [SkippableFact]
    public async Task A_change_waits_for_commit_and_rollback_undoes_it_then_a_new_one_opens()
    {
        Require();
        var tx = await Run("delete from auto_probe"); // the missing WHERE
        Assert.Equal("changed", State(tx));
        Assert.Equal(3, tx.GetProperty("rowsChanged").GetInt64());

        var after = await Rollback();
        Assert.Equal("open", State(after)); // a fresh one, straight away
        Assert.True(Automatic(after));
        Assert.Equal(0, after.GetProperty("rowsChanged").GetInt64());
        Assert.Equal(3, (await Run("delete from auto_probe")).GetProperty("rowsChanged").GetInt64()); // the rows came back
        await Rollback();
    }

    [SkippableFact]
    public async Task Commit_keeps_the_change_and_opens_the_next_one()
    {
        Require();
        await Run("update auto_probe set x = 10 where x = 1");
        var after = await Commit();
        Assert.Equal("open", State(after));
        Assert.True(Automatic(after));
        Assert.Equal(1, (await Run("update auto_probe set x = 1 where x = 10")).GetProperty("rowsChanged").GetInt64()); // committed
        await Commit();
    }

    [SkippableFact]
    public async Task A_typed_commit_opens_the_next_one_too()
    {
        Require();
        await Run("update auto_probe set x = x where x = 2");
        var tx = await Run("commit");
        Assert.Equal("open", State(tx));
        Assert.True(Automatic(tx));
        Assert.Equal(0, tx.GetProperty("statements").GetInt32());
    }

    [SkippableFact]
    public async Task By_default_a_failure_is_undone_and_the_automatic_transaction_carries_on()
    {
        Require();
        await Run("delete from auto_probe where x = 1");
        var tx = await Run("insert into auto_probe values ('not a number')");
        Assert.Equal("changed", State(tx));                // the delete is still pending, nothing is red
        Assert.True(tx.GetProperty("statementUndone").GetBoolean());
        Assert.False(tx.GetProperty("autoReset").GetBoolean());
        Assert.True(Automatic(tx));
        Assert.Equal("open", State(await Rollback()));
    }

    [SkippableFact]
    public async Task With_abort_chosen_a_failure_with_nothing_changed_is_rolled_back_and_started_again()
    {
        Require();
        OnError.Abort(_pg);
        var tx = await Run("select no_such_column from auto_probe");
        Assert.Equal("open", State(tx));
        Assert.True(tx.GetProperty("autoReset").GetBoolean());
    }

    [SkippableFact]
    public async Task With_abort_chosen_a_failure_after_a_change_leaves_it_aborted_for_the_user()
    {
        Require();
        OnError.Abort(_pg);
        await Run("delete from auto_probe where x = 1");
        var tx = await Run("insert into auto_probe values ('not a number')");
        Assert.Equal("failed", State(tx)); // the delete is in there: only the user may throw it away
        Assert.False(tx.GetProperty("autoReset").GetBoolean());
        Assert.Equal("open", State(await Rollback()));
    }

    [SkippableFact]
    public async Task A_statement_refused_in_a_transaction_runs_when_nothing_has_changed()
    {
        Require();
        var tx = await Run("vacuum auto_probe");
        Assert.Equal("open", State(tx)); // ended quietly, ran, and a new one opened
        Assert.DoesNotContain(_sink.Events, e => e.GetProperty("type").GetString() == "error");
    }

    [SkippableFact]
    public async Task A_statement_refused_in_a_transaction_is_not_run_while_there_are_changes()
    {
        Require();
        await Run("delete from auto_probe where x = 3");
        var reply = await Send(new { type = "execute", connectionId = _connectionId, sql = "vacuum auto_probe" });
        Assert.Equal("error", reply.GetProperty("type").GetString());
        Assert.Contains("Commit or roll back first", reply.GetProperty("message").GetString());
        Assert.Equal("changed", State(LastTx())); // nothing was committed behind the user's back
        await Rollback();
    }

    [SkippableFact]
    public async Task A_read_only_connection_never_opens_one()
    {
        Require();
        var ro = await Open(readOnly: true);
        var tx = await Run("select 1", ro);
        Assert.Equal("none", State(tx));
    }
}
