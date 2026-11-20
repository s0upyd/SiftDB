using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Who's blocking me (design section 14, F3), on real Postgres sessions: one sheet's open transaction holds a row lock,
/// another sheet's UPDATE waits for it. Needs SIFTDB_SPIKE_CONN. One uniquely named table, dropped at the end.
/// </summary>
public class LockTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly ConcurrentQueue<string> _events = new();
        public void SendEvent(string json) => _events.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch b) { }
        public List<JsonElement> Of(string type) => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
            .Where(e => e.GetProperty("type").GetString() == type).ToList();
    }

    private static readonly LockWatchTiming Fast = new(
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _table = "siftdb_lock_" + Guid.NewGuid().ToString("N")[..8];
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request;

    public Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(_cs)) _engine = new EngineHost([new PostgresProvider()], _sink) { LockTiming = Fast };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        await Step("dispose the engine", _engine.DisposeAsync().AsTask());
        await using var e = new EngineHost([new PostgresProvider()], _sink); // the same sink: Connect reads the id from it
        var id = await Connect(e);
        await Send(e, new { type = "execute", connectionId = id, sql = $"drop table if exists {_table}" });
    }

    /// <summary>A step that must end within 15 s: a hang fails the test with its name rather than stalling the run.</summary>
    private static async Task Step(string name, Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { throw new TimeoutException($"Hung: {name}"); }
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private Task Send(EngineHost e, object command)
    {
        var json = JsonSerializer.SerializeToNode(command)!.AsObject();
        json["id"] = ++_request;
        return e.HandleCommandAsync(json.ToJsonString());
    }

    private async Task<int> Connect(EngineHost e)
    {
        var before = _sink.Of("connected").Count;
        await Send(e, new { type = "connect", provider = "postgres", connectionString = _cs });
        return _sink.Of("connected").Count > before ? _sink.Of("connected").Last().GetProperty("connectionId").GetInt32() : 0;
    }

    /// <summary>Runs a statement to its end (no rows to fetch for these), waiting for the transaction state after it.</summary>
    private async Task Run(int connectionId, string sql)
    {
        var before = _sink.Of("transaction").Count(t => t.GetProperty("connectionId").GetInt32() == connectionId);
        await Send(_engine!, new { type = "execute", connectionId, sql });
        await Until(() => _sink.Of("transaction").Count(t => t.GetProperty("connectionId").GetInt32() == connectionId) > before);
    }

    private static async Task Until(Func<bool> done, int ms = 10_000)
    {
        for (var waited = 0; !done() && waited < ms; waited += 25) await Task.Delay(25);
        Assert.True(done(), "timed out");
    }

    private JsonElement? Last(string type, int connectionId) =>
        _sink.Of(type).LastOrDefault(e => e.GetProperty("connectionId").GetInt32() == connectionId) is { ValueKind: JsonValueKind.Object } e ? e : null;

    [SkippableFact]
    public async Task A_waiting_update_names_its_blocker_who_hears_it_blocks_one_and_ending_it_frees_the_wait()
    {
        Require();
        var holder = await Connect(_engine!);
        var waiter = await Connect(_engine!);
        await Run(holder, $"create table {_table} (id int primary key, n int)");
        await Run(holder, $"insert into {_table} values (1, 0)");
        await Run(holder, "begin");
        await Run(holder, $"update {_table} set n = 1 where id = 1"); // holds the row lock, idle in transaction

        // The waiter's UPDATE cannot finish until the holder's transaction ends: not awaited.
        var waiting = Send(_engine!, new { type = "execute", connectionId = waiter, sql = $"update {_table} set n = 2 where id = 1" });

        await Until(() => Last("blockers", waiter) is { } b && b.GetProperty("blockers").GetArrayLength() == 1);
        var blocker = Last("blockers", waiter)!.Value.GetProperty("blockers")[0];
        Assert.False(blocker.GetProperty("running").GetBoolean());              // idle in transaction: only ending it helps
        Assert.Equal("idle in transaction", blocker.GetProperty("state").GetString());
        Assert.True(blocker.GetProperty("canSignal").GetBoolean());             // the same role
        // Its last statement on the server is the sheet's own state check, not the user's UPDATE: left out, not shown as theirs.
        Assert.Equal(JsonValueKind.Null, blocker.GetProperty("query").ValueKind);
        Assert.True(blocker.GetProperty("inTransactionSeconds").GetDouble() >= 0);
        // The blocker is another of SiftDB's own sessions: the event says which, so the UI can name its sheet.
        var mine = Assert.Single(Last("blockers", waiter)!.Value.GetProperty("mine").EnumerateArray());
        Assert.Equal(holder, mine.GetProperty("connectionId").GetInt32());
        Assert.Equal(blocker.GetProperty("pid").GetInt32(), mine.GetProperty("pid").GetInt32());

        // The holder is idle in its open transaction: it hears that it blocks one session, the waiter.
        await Until(() => Last("blocking", holder) is { } b && b.GetProperty("sessions").GetArrayLength() == 1);
        Assert.True(Last("blocking", holder)!.Value.GetProperty("sessions")[0].GetProperty("waitingForLock").GetBoolean());
        Assert.Equal(waiter, Assert.Single(Last("blocking", holder)!.Value.GetProperty("mine").EnumerateArray()).GetProperty("connectionId").GetInt32());

        // End the holder's session from the waiter's sheet: its transaction is rolled back, and the waiting UPDATE runs.
        var pid = blocker.GetProperty("pid").GetInt32();
        await Step("end the holder", Send(_engine!, new { type = "cancelOther", connectionId = waiter, pid, terminate = true }));
        Assert.Contains(_sink.Of("ok"), e => e.GetProperty("replyTo").GetInt32() == _request);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => Last("blockers", waiter) is { } b && b.GetProperty("blockers").GetArrayLength() == 0);
    }

    [Theory]
    [InlineData("select pg_current_xact_id_if_assigned() is not null, transaction_timestamp()", true)]
    [InlineData("release savepoint siftdb_statement;select pg_current_xact_id_if_assigned() is not null, transaction_timestamp()", true)]
    [InlineData("rollback to savepoint siftdb_peek; release savepoint siftdb_peek", true)]
    [InlineData("update orders set n = 1 where id = 1", false)]
    [InlineData("release savepoint siftdb_statement; update orders set n = 1", false)] // the user's statement is in it
    [InlineData("savepoint mine", false)]
    public void A_sheets_own_bookkeeping_is_not_shown_as_its_query(string query, bool housekeeping) =>
        Assert.Equal(housekeeping, PostgresLocks.IsSiftDbHousekeeping(query));

    [SkippableFact]
    public async Task A_slow_statement_that_waits_for_no_one_says_nothing()
    {
        Require();
        var sheet = await Connect(_engine!);
        await Run(sheet, "select pg_sleep(0.8)");
        Assert.Null(Last("blockers", sheet));
    }

    [SkippableFact]
    public async Task Cancelling_a_session_that_is_gone_says_so()
    {
        Require();
        var sheet = await Connect(_engine!);
        await Send(_engine!, new { type = "cancelOther", connectionId = sheet, pid = 999_999, terminate = false });
        var error = _sink.Of("error").Last();
        Assert.Equal(_request, error.GetProperty("replyTo").GetInt32());
        Assert.Contains("no longer there", error.GetProperty("message").GetString());
    }
}
