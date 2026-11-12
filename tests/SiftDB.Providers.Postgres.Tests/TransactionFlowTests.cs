using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// The transaction bar's whole path against a real server: engine, Npgsql provider, protocol status. Needs
/// SIFTDB_SPIKE_CONN. Uses a temp table, which lives and dies with the session, so nothing is left behind.
/// </summary>
public class TransactionFlowTests : IAsyncLifetime
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
    private readonly Sink _sink = new();
    private readonly PostgresProvider _pg = new();
    private EngineHost? _engine;
    private int _nextQuery;     // mirrors the engine's query ids: bumped only when a statement is executed
    private int _nextRequest;   // ids for transaction commands

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([_pg], _sink);
        await _engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "postgres", connectionString = _cs }));
        await Run("create temp table flow_probe (x int)");
    }

    public async Task DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private int StateEvents() => _sink.Of("transaction").Count + _sink.Of("transactionUnknown").Count;

    /// <summary>Executes one statement as the UI does, then waits until the engine has reported the transaction state after it.</summary>
    private async Task<(int QueryId, JsonElement? Complete)> Run(string sql)
    {
        var queryId = ++_nextQuery;
        var requestId = ++_nextRequest; // unique per command, whatever happens to the query ids
        var before = StateEvents();
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = requestId, connectionId = 1, sql }));

        // If the statement failed outright there is no query to fetch.
        var failed = _sink.Of("error").Any(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == requestId);
        if (failed) _nextQuery--; // the engine hands out a query id only once the statement has started
        for (var i = 0; !failed && i < 50 && !_sink.Of("complete").Any(e => e.GetProperty("queryId").GetInt32() == queryId); i++)
            await _engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId }));

        for (var i = 0; i < 300 && StateEvents() <= before; i++) await Task.Delay(10);
        var complete = _sink.Of("complete").Cast<JsonElement?>().FirstOrDefault(e => e!.Value.GetProperty("queryId").GetInt32() == queryId);
        return (queryId, complete);
    }

    private JsonElement Tx() => _sink.Of("transaction").Last();
    private string State() => Tx().GetProperty("state").GetString()!;

    private async Task Do(string action)
    {
        var id = ++_nextRequest;
        await _engine!.HandleCommandAsync(JsonSerializer.Serialize(new { type = "transaction", id, connectionId = 1, action }));
    }

    /// <summary>count(*) through the engine's own session (the temp table is session-local), read from the batch it sent.</summary>
    private async Task<long> Count()
    {
        var (queryId, _) = await Run("select count(*) from flow_probe");
        var d = _sink.FirstBatch[queryId];
        var valueOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12 + 4)); // column 0's data offset
        return BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(valueOffset));
    }

    [SkippableFact]
    public async Task A_connection_starts_with_no_transaction()
    {
        Require();
        Assert.Equal("none", State());
        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task The_whole_story_none_open_changed_committed()
    {
        Require();
        await Do("begin");
        Assert.Equal("open", State());
        Assert.NotEqual(JsonValueKind.Null, Tx().GetProperty("startedAt").ValueKind);

        await Run("select 1");
        Assert.Equal("open", State());
        Assert.Equal(1, Tx().GetProperty("statements").GetInt32());

        await Run("insert into flow_probe values (1), (2), (3)");
        Assert.Equal("changed", State());
        Assert.Equal(2, Tx().GetProperty("statements").GetInt32());
        Assert.Equal(3, Tx().GetProperty("rowsChanged").GetInt64());

        await Do("commit");
        Assert.Equal("none", State());
        Assert.Equal(0, Tx().GetProperty("statements").GetInt32());
        Assert.Equal(0, Tx().GetProperty("rowsChanged").GetInt64());
        Assert.Equal(3, await Count());                     // committed
    }

    [SkippableFact]
    public async Task Rollback_discards_the_changes()
    {
        Require();
        await Do("begin");
        await Run("insert into flow_probe values (1)");
        Assert.Equal("changed", State());
        await Do("rollback");
        Assert.Equal("none", State());
        Assert.Equal(0, await Count());
    }

    [SkippableFact]
    public async Task Typed_BEGIN_and_COMMIT_drive_the_bar_exactly_like_the_buttons()
    {
        Require();
        await Run("begin");
        Assert.Equal("open", State());
        Assert.Equal(0, Tx().GetProperty("statements").GetInt32());      // the BEGIN itself is not counted

        await Run("insert into flow_probe values (1)");
        Assert.Equal("changed", State());

        await Run("commit");
        Assert.Equal("none", State());
        Assert.Equal(1, await Count());
    }

    [SkippableFact]
    public async Task With_abort_chosen_a_failing_statement_turns_the_bar_red_until_rollback()
    {
        Require();
        OnError.Abort(_pg);
        await Do("begin");
        await Run("insert into flow_probe values (1)");
        await Run("select 1/0");

        Assert.Equal("failed", State());
        Assert.Equal(1, Tx().GetProperty("rowsChanged").GetInt64());      // what was done before the error is still on the books
        Assert.Contains(_sink.Of("error"), e => e.GetProperty("message").GetString()!.Contains("division by zero"));

        await Do("rollback");
        Assert.Equal("none", State());
        Assert.Equal(0, await Count());
    }

    // ---- undoing just the failed statement (the default; design section 7) ----

    private bool Undone() => Tx().GetProperty("statementUndone").GetBoolean();

    [SkippableTheory]
    [InlineData("select 1/0")]                                                       // fails as it starts
    [InlineData("selec 1")]                                                          // a typo: does not even parse
    [InlineData("select case when x = 90000 then 1/0 else x end from generate_series(1, 100000) x")] // fails part way through the rows
    [InlineData("insert into flow_probe values (1), (2), ('three')")]              // a write that fails: none of it stays
    public async Task By_default_a_failed_statement_is_undone_and_the_transaction_keeps_its_changes(string failing)
    {
        Require();
        await Do("begin");
        await Run("insert into flow_probe values (1)");
        await Run(failing);

        Assert.Equal("changed", State());                                   // not red: carry on
        Assert.True(Undone());
        Assert.Equal(1, Tx().GetProperty("statements").GetInt32());        // the undone one does not count
        Assert.Equal(1, Tx().GetProperty("rowsChanged").GetInt64());
        Assert.Empty(_sink.Of("transactionUnknown"));

        await Run("insert into flow_probe values (2)");                     // the transaction still works
        Assert.False(Undone());
        await Do("commit");
        Assert.Equal(2, await Count());
    }

    [SkippableFact]
    public async Task A_failure_with_nothing_changed_yet_leaves_the_transaction_open()
    {
        Require();
        await Do("begin");
        await Run("select no_such_column from flow_probe");
        Assert.Equal("open", State());
        Assert.True(Undone());
        await Do("rollback");
    }

    [SkippableFact]
    public async Task Typed_savepoints_act_on_the_users_transaction_not_the_guard()
    {
        Require();
        await Do("begin");
        await Run("insert into flow_probe values (1)");
        await Run("savepoint mine");
        await Run("insert into flow_probe values (2)");
        await Run("select 1/0");                                            // undone: "mine" is still there
        await Run("rollback to savepoint mine");                            // undoes the second insert
        Assert.Equal("changed", State());
        Assert.DoesNotContain(_sink.Of("error"), e => !e.GetProperty("message").GetString()!.Contains("division by zero"));
        await Do("commit");
        Assert.Equal(1, await Count());
    }

    [SkippableFact]
    public async Task A_typed_commit_is_not_guarded_and_commits()
    {
        Require();
        await Run("begin");
        await Run("insert into flow_probe values (1)");
        await Run("commit");
        Assert.Equal("none", State());
        Assert.Empty(_sink.Of("transactionUnknown"));
        Assert.Equal(1, await Count());
    }

    [SkippableFact]
    public async Task Choosing_abort_applies_from_the_next_statement_of_an_open_session()
    {
        Require();
        await Do("begin");
        await Run("select 1/0");
        Assert.Equal("open", State());                                      // undone, the default
        OnError.Abort(_pg);
        await Run("select 1/0");
        Assert.Equal("failed", State());                                    // now Postgres's own behaviour
        Assert.False(Undone());
        await Do("rollback");
    }

    [SkippableFact]
    public async Task An_error_outside_a_transaction_leaves_the_bar_off()
    {
        Require();
        await Run("select 1/0");
        Assert.Equal("none", State());
        Assert.False(Undone());                                             // nothing to guard outside a transaction
        Assert.Empty(_sink.Of("transactionUnknown"));
    }

    [SkippableFact]
    public async Task A_real_driver_reports_rows_affected_for_writes_and_not_for_selects()
    {
        Require();
        var write = await Run("insert into flow_probe values (1), (2)");
        Assert.Equal(2, write.Complete!.Value.GetProperty("rowsAffected").GetInt64());

        var read = await Run("select * from flow_probe");
        Assert.Equal(JsonValueKind.Null, read.Complete!.Value.GetProperty("rowsAffected").ValueKind);
    }

    [SkippableFact]
    public async Task A_new_statement_while_one_is_still_streaming_does_not_hang_or_corrupt_the_session()
    {
        Require();
        await _engine!.HandleCommandAsync("""{"type":"execute","id":500,"connectionId":1,"sql":"select generate_series(1, 1000000)"}""");
        _nextQuery++; // that statement was query 1 from the engine's point of view
        // Abandon it unfetched and run something else on the same session.
        var next = Run("select 42");
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(10_000)));
        Assert.NotNull((await next).Complete);
    }
}
