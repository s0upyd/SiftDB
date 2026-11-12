using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Two sheets means two real Postgres sessions on one engine. Needs SIFTDB_SPIKE_CONN. The only thing created outside a
/// session's temp space is one uniquely named table, which is dropped at the end.
/// </summary>
public class MultiSessionTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();
        public readonly ConcurrentDictionary<int, byte[]> FirstBatch = new();
        public void SendEvent(string json) { lock (_gate) _events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch b) => FirstBatch.TryAdd(queryId, b.Data);
        public List<JsonElement> Of(string type)
        {
            lock (_gate)
                return _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
                    .Where(e => e.GetProperty("type").GetString() == type).ToList();
        }
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _table = "siftdb_ms_" + Guid.NewGuid().ToString("N")[..8];
    private readonly Sink _sink = new();
    private readonly PostgresProvider _pg = new();
    private EngineHost? _engine;
    private int _request;
    private int _a, _b;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([_pg], _sink);
        _a = await Connect();
        _b = await Connect();
        await Run(_b, $"create table {_table} (x int)");
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        try { await Run(_b, $"drop table if exists {_table}"); } catch { /* the engine may already be torn down by a test */ }
        await _engine.DisposeAsync();
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task<int> Connect()
    {
        var id = ++_request;
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id, provider = "postgres", connectionString = _cs }));
        return _sink.Of("connected").Single(e => e.GetProperty("replyTo").GetInt32() == id).GetProperty("connectionId").GetInt32();
    }

    private int StateEvents() => _sink.Of("transaction").Count + _sink.Of("transactionUnknown").Count;

    /// <summary>Executes on one session as the UI does and waits for the transaction state after it. Returns the engine's query id, or 0 if it failed to start.</summary>
    private async Task<int> Run(int connectionId, string sql)
    {
        var request = ++_request;
        var before = StateEvents();
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = request, connectionId, sql }));
        if (_sink.Of("error").Any(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == request)) { await SettleAfter(before); return 0; }

        var queryId = _sink.Of("columns").Single(e => e.GetProperty("replyTo").GetInt32() == request).GetProperty("queryId").GetInt32();
        for (var i = 0; i < 50 && !_sink.Of("complete").Any(e => e.GetProperty("queryId").GetInt32() == queryId)
                           && !_sink.Of("error").Any(e => e.TryGetProperty("queryId", out var q) && q.GetInt32() == queryId); i++)
            await _engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId }));
        await SettleAfter(before);
        return queryId;
    }

    private async Task SettleAfter(int before)
    {
        for (var i = 0; i < 300 && StateEvents() <= before; i++) await Task.Delay(10);
    }

    private async Task Tx(int connectionId, string action)
    {
        var request = ++_request;
        var before = StateEvents();
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "transaction", id = request, connectionId, action }));
        await SettleAfter(before);
    }

    private string State(int connectionId) =>
        _sink.Of("transaction").Last(e => e.GetProperty("connectionId").GetInt32() == connectionId).GetProperty("state").GetString()!;

    private async Task<long> Count(int connectionId)
    {
        var queryId = await Run(connectionId, $"select count(*) from {_table}");
        if (queryId == 0)
            Assert.Fail("The count did not start: " + string.Join(" | ", _sink.Of("error").Select(e => e.GetProperty("message").GetString())));
        Assert.True(_sink.FirstBatch.TryGetValue(queryId, out var d),
            "No rows arrived for the count. Events: " + string.Join(" | ", _sink.Of("error").Concat(_sink.Of("complete")).Select(e => e.ToString())));
        return BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12 + 4))));
    }

    [SkippableFact]
    public async Task Each_session_has_its_own_temp_tables()
    {
        Require();
        await Run(_a, "create temp table only_in_a (x int)");

        Assert.NotEqual(0, await Run(_a, "select * from only_in_a"));   // A can use it
        Assert.Equal(0, await Run(_b, "select * from only_in_a"));      // B cannot: "relation does not exist" (0 = failed to start)
    }

    [SkippableFact]
    public async Task A_transaction_in_one_session_does_not_show_in_the_other()
    {
        Require();
        await Tx(_a, "begin");
        await Run(_a, $"insert into {_table} values (1)");

        Assert.Equal("changed", State(_a));
        Assert.Equal("none", State(_b));
        Assert.Equal(0, await Count(_b));              // A's change is invisible until it commits
        Assert.Equal("none", State(_b));

        await Tx(_a, "commit");
        Assert.Equal("none", State(_a));
        Assert.Equal(1, await Count(_b));              // now it is
    }

    [SkippableFact]
    public async Task With_abort_chosen_an_error_aborts_only_the_session_it_happened_in()
    {
        Require();
        OnError.Abort(_pg);
        await Tx(_a, "begin");
        await Tx(_b, "begin");
        await Run(_a, "select 1/0");

        Assert.Equal("failed", State(_a));
        Assert.Equal("open", State(_b));               // B is untouched
        await Tx(_a, "rollback");
        await Tx(_b, "rollback");
    }

    [SkippableFact]
    public async Task Closing_a_session_with_uncommitted_changes_rolls_them_back()
    {
        Require();
        await Tx(_a, "begin");
        await Run(_a, $"insert into {_table} values (1), (2), (3)");
        Assert.Equal("changed", State(_a));

        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "disconnect", id = ++_request, connectionId = _a }));

        Assert.Equal(0, await Count(_b));              // thrown away, as the close-tab warning says it will be
    }

    [SkippableFact]
    public async Task Closing_a_session_after_a_commit_keeps_the_changes()
    {
        Require();
        await Tx(_a, "begin");
        await Run(_a, $"insert into {_table} values (1), (2)");
        await Tx(_a, "commit");

        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "disconnect", id = ++_request, connectionId = _a }));

        Assert.Equal(2, await Count(_b));
    }

    /// <summary>Starts a statement that Postgres will not answer for a minute and waits until it is truly stuck.</summary>
    private async Task<(Task Execute, int RequestId)> StartSlowStatement(int connectionId)
    {
        var request = ++_request;
        var execute = _engine!.HandleCommandAsync(JsonSerializer.Serialize(new
        {
            type = "execute", id = request, connectionId, sql = "select pg_sleep(60)",
        }));
        await Task.Delay(600);
        Assert.False(execute.IsCompleted, "the statement should still be waiting on the server");
        return (execute, request);
    }

    [SkippableFact]
    public async Task Cancelling_a_slow_statement_that_has_returned_nothing_frees_the_session()
    {
        Require();
        var (execute, request) = await StartSlowStatement(_a);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "cancel", connectionId = _a }));
        Assert.Same(execute, await Task.WhenAny(execute, Task.Delay(15_000)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:F1}s, not the 60s sleep");
        Assert.Contains(_sink.Of("cancelled"), e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == request);
        Assert.NotEqual(0, await Run(_a, "select 1"));                 // the session works again
    }

    [SkippableFact]
    public async Task By_default_cancelling_inside_a_transaction_undoes_the_statement_and_keeps_the_changes()
    {
        Require();
        await Tx(_a, "begin");
        await Run(_a, $"insert into {_table} values (1)");
        var (execute, _) = await StartSlowStatement(_a);

        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "cancel", connectionId = _a }));
        Assert.Same(execute, await Task.WhenAny(execute, Task.Delay(15_000)));

        Assert.Equal("changed", State(_a));                             // the insert is still pending
        await Tx(_a, "commit");
        Assert.Equal(1, await Count(_b));
    }

    [SkippableFact]
    public async Task With_abort_chosen_cancelling_inside_a_transaction_aborts_it_and_the_bar_says_so()
    {
        Require();
        OnError.Abort(_pg);
        await Tx(_a, "begin");
        var (execute, _) = await StartSlowStatement(_a);

        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "cancel", connectionId = _a }));
        Assert.Same(execute, await Task.WhenAny(execute, Task.Delay(15_000)));

        Assert.Equal("failed", State(_a));                              // Postgres aborts the transaction on a cancel
        await Tx(_a, "rollback");
        Assert.Equal("none", State(_a));
    }

    [SkippableFact]
    public async Task Closing_a_session_while_a_slow_statement_is_running_does_not_wait_it_out()
    {
        Require();
        var (execute, _) = await StartSlowStatement(_a);

        var close = _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "disconnect", id = ++_request, connectionId = _a }));
        Assert.Same(close, await Task.WhenAny(close, Task.Delay(15_000)));
        Assert.Same(execute, await Task.WhenAny(execute, Task.Delay(15_000)));

        Assert.Equal(0, await Count(_b));                              // the other session is unaffected
    }

    [SkippableFact]
    public async Task Closing_a_session_in_the_middle_of_a_big_stream_does_not_hang_or_disturb_the_others()
    {
        Require();
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new
        {
            type = "execute", id = ++_request, connectionId = _a, sql = "select generate_series(1, 5000000)",
        }));                                           // streaming, nobody fetching

        var close = _engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "disconnect", id = ++_request, connectionId = _a }));
        Assert.Same(close, await Task.WhenAny(close, Task.Delay(15_000)));

        Assert.Equal(0, await Count(_b));              // B carries on
    }
}
