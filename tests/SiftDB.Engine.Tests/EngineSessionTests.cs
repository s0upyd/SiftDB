using System.Data;
using System.Data.Common;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>Several sessions on one engine (one per sheet): they must be independent, and closing one must be clean.</summary>
public class EngineSessionTests
{
    /// <summary>Each OpenAsync is a new session with its own transaction state, and remembers whether it was closed.</summary>
    private sealed class Provider : IDatabaseProvider
    {
        public readonly List<Session> Sessions = new();
        public string Id => "fake";
        public int ContractVersion => ProviderContract.CurrentVersion;
        public string DisplayName => Id;
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) => throw new NotSupportedException();
        public ParsedConnection ParseConnectionString(string connectionString) => throw new NotSupportedException();
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;
        public string? CheckReadOnly(string sql) => null;
        public bool CannotRunInTransaction(string sql) => false;
        public ISqlParser Parser => throw new NotSupportedException();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();

        public Task<IProviderConnection> OpenAsync(string cs, ConnectionOptions options, CancellationToken ct)
        {
            var s = new Session();
            lock (Sessions) Sessions.Add(s);
            return Task.FromResult<IProviderConnection>(s);
        }
    }

    private sealed class Session : IProviderConnection, ITransactionMonitor
    {
        public TransactionState State = TransactionState.None;
        public volatile bool Disposed;
        public int Rows = 3;
        public ITransactionMonitor Transactions => this;
        public bool CanDetectWrites => true;
        public Task BeginAsync(CancellationToken ct) { State = TransactionState.Open; return Task.CompletedTask; }
        public Task CommitAsync(CancellationToken ct) { State = TransactionState.None; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) { State = TransactionState.None; return Task.CompletedTask; }
        public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(new TransactionSnapshot(State, State == TransactionState.None ? null : DateTimeOffset.UnixEpoch));
        public Task<DatabaseSchema> IntrospectAsync(CancellationToken ct) => Task.FromResult(new DatabaseSchema([]));

        /// <summary>When set, a statement does not answer until it is cancelled: a slow aggregate, pg_sleep.</summary>
        public volatile bool HangOnExecute;
        public volatile int Started;

        public async Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            if (HangOnExecute) await Task.Delay(Timeout.Infinite, ct);
            var t = new DataTable();
            t.Columns.Add("x", typeof(int));
            for (var i = 0; i < Rows; i++) t.Rows.Add(i);
            return t.CreateDataReader();
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Capture : IEngineOutput
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();
        private int _batches;
        public void SendEvent(string json) { lock (_gate) _events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch b) { lock (_gate) _batches++; }
        /// <summary>Everything the engine has emitted: events and row batches alike.</summary>
        public int Count { get { lock (_gate) return _events.Count + _batches; } }
        public List<JsonElement> Of(string type)
        {
            lock (_gate)
                return _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
                    .Where(e => e.GetProperty("type").GetString() == type).ToList();
        }
    }

    private static async Task<int> Connect(EngineHost engine, Capture cap, int requestId)
    {
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = requestId, provider = "fake", connectionString = "x" }));
        return cap.Of("connected").Single(e => e.GetProperty("replyTo").GetInt32() == requestId).GetProperty("connectionId").GetInt32();
    }

    private static Task Send(EngineHost engine, object cmd) => engine.HandleCommandAsync(JsonSerializer.Serialize(cmd));

    private static async Task<T> Within<T>(Task<T> task, int ms = 5000)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) throw new TimeoutException("did not finish in time (a lock leaked or a stream never wound down)");
        return await task;
    }

    private static async Task Within(Task task, int ms = 5000) => await Within(task.ContinueWith(_ => 0), ms);

    // ---- independence ----

    [Fact]
    public async Task Each_connect_is_its_own_session()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        var b = await Connect(engine, cap, 2);

        Assert.NotEqual(a, b);
        Assert.Equal(2, p.Sessions.Count);
    }

    [Fact]
    public async Task A_transaction_in_one_session_does_not_touch_the_other()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        var b = await Connect(engine, cap, 2);

        await Send(engine, new { type = "transaction", id = 3, connectionId = a, action = "begin" });

        string LastState(int conn) => cap.Of("transaction").Last(e => e.GetProperty("connectionId").GetInt32() == conn).GetProperty("state").GetString()!;
        Assert.Equal("open", LastState(a));
        Assert.Equal("none", LastState(b));
        Assert.Equal(TransactionState.Open, p.Sessions[0].State);
        Assert.Equal(TransactionState.None, p.Sessions[1].State);
    }

    [Fact]
    public async Task Statements_run_in_the_session_they_were_sent_to_and_do_not_wait_for_each_other()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        var b = await Connect(engine, cap, 2);
        p.Sessions[0].Rows = 200_000; // session A is busy streaming and nobody is fetching

        await Send(engine, new { type = "execute", id = 10, connectionId = a, sql = "select" });
        var other = Send(engine, new { type = "execute", id = 11, connectionId = b, sql = "select" });

        await Within(other);                       // would hang if the sessions shared one lock
        Assert.Equal(2, cap.Of("columns").Count);
    }

    // ---- closing a session ----

    [Fact]
    public async Task Disconnect_closes_the_session_replies_ok_and_forgets_the_id()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);

        await Send(engine, new { type = "disconnect", id = 2, connectionId = a });

        Assert.True(p.Sessions[0].Disposed);
        Assert.Contains(cap.Of("ok"), e => e.GetProperty("replyTo").GetInt32() == 2);

        await Send(engine, new { type = "execute", id = 3, connectionId = a, sql = "select" });
        Assert.Contains(cap.Of("error"), e => e.GetProperty("replyTo").GetInt32() == 3);
    }

    [Fact]
    public async Task Disconnect_leaves_the_other_sessions_alone()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        var b = await Connect(engine, cap, 2);

        await Send(engine, new { type = "disconnect", id = 3, connectionId = a });

        Assert.True(p.Sessions[0].Disposed);
        Assert.False(p.Sessions[1].Disposed);
        await Send(engine, new { type = "execute", id = 4, connectionId = b, sql = "select" });
        Assert.Equal(1, cap.Of("columns").Count);
    }

    [Fact]
    public async Task Disconnect_during_a_stream_cancels_it_and_still_finishes()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        p.Sessions[0].Rows = 200_000;
        await Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select" }); // streaming, never fetched

        await Within(Send(engine, new { type = "disconnect", id = 3, connectionId = a }));

        Assert.True(p.Sessions[0].Disposed);
        Assert.Contains(cap.Of("ok"), e => e.GetProperty("replyTo").GetInt32() == 3);
    }

    [Fact]
    public async Task Disconnecting_an_unknown_session_is_an_error()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new Provider()], cap);
        await Send(engine, new { type = "disconnect", id = 9, connectionId = 77 });
        Assert.Contains(cap.Of("error"), e => e.GetProperty("replyTo").GetInt32() == 9);
    }

    [Fact]
    public async Task Disposing_the_engine_closes_every_session()
    {
        var p = new Provider();
        var cap = new Capture();
        var engine = new EngineHost([p], cap);
        await Connect(engine, cap, 1);
        await Connect(engine, cap, 2);
        await engine.DisposeAsync();
        Assert.All(p.Sessions, s => Assert.True(s.Disposed));
    }

    // ---- a statement that has not answered yet ----
    // It has no query id (nothing has come back), so it cannot be cancelled by one. These are the cases a slow aggregate or
    // pg_sleep creates: the session lock is held, so until it is cancelled nothing else on that sheet works.

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("never happened: " + what);
    }

    [Fact]
    public async Task Cancel_by_connection_stops_a_statement_that_has_not_answered_and_frees_the_session()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        p.Sessions[0].HangOnExecute = true;

        var execute = Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select pg_sleep(60)" });
        await WaitUntil(() => p.Sessions[0].Started == 1, "the statement to start");
        Assert.False(execute.IsCompleted);                       // it really is stuck

        await Send(engine, new { type = "cancel", connectionId = a });
        await Within(execute);

        Assert.Contains(cap.Of("cancelled"), e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == 2);
        Assert.Empty(cap.Of("error"));                            // a cancel is not an error

        p.Sessions[0].HangOnExecute = false;                      // and the session works again
        await Within(Send(engine, new { type = "execute", id = 3, connectionId = a, sql = "select" }));
        Assert.Contains(cap.Of("columns"), e => e.GetProperty("replyTo").GetInt32() == 3);
    }

    [Fact]
    public async Task Closing_a_session_whose_statement_has_not_answered_does_not_wait_for_it()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        p.Sessions[0].HangOnExecute = true;
        var execute = Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select pg_sleep(60)" });
        await WaitUntil(() => p.Sessions[0].Started == 1, "the statement to start");

        await Within(Send(engine, new { type = "disconnect", id = 3, connectionId = a }));   // would hang on the lock otherwise
        await Within(execute);

        Assert.True(p.Sessions[0].Disposed);
    }

    [Fact]
    public async Task A_new_statement_replaces_one_that_has_not_answered()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        p.Sessions[0].HangOnExecute = true;
        var first = Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select pg_sleep(60)" });
        await WaitUntil(() => p.Sessions[0].Started == 1, "the first statement to start");

        p.Sessions[0].HangOnExecute = false;
        await Within(Send(engine, new { type = "execute", id = 3, connectionId = a, sql = "select 1" }));
        await Within(first);

        Assert.Contains(cap.Of("cancelled"), e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == 2);
        Assert.Contains(cap.Of("columns"), e => e.GetProperty("replyTo").GetInt32() == 3);
    }

    [Fact]
    public async Task Begin_and_commit_are_not_held_up_by_a_statement_that_has_not_answered()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);
        p.Sessions[0].HangOnExecute = true;
        var execute = Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select pg_sleep(60)" });
        await WaitUntil(() => p.Sessions[0].Started == 1, "the statement to start");

        await Within(Send(engine, new { type = "transaction", id = 3, connectionId = a, action = "begin" }));
        await Within(execute);

        Assert.Equal(TransactionState.Open, p.Sessions[0].State);
    }

    [Fact]
    public async Task Cancelling_something_that_is_not_running_is_harmless()
    {
        var p = new Provider();
        var cap = new Capture();
        await using var engine = new EngineHost([p], cap);
        var a = await Connect(engine, cap, 1);

        await Send(engine, new { type = "cancel", connectionId = a });        // idle session
        await Send(engine, new { type = "cancel", connectionId = 99 });       // no such session
        await Send(engine, new { type = "cancel", queryId = 12345 });         // no such query

        Assert.Empty(cap.Of("error"));
        await Within(Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select" }));   // still fine
    }

    // ---- schema refresh without the payload ----

    [Fact]
    public async Task Introspect_can_refresh_the_cache_without_sending_the_schema()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new Provider()], cap);
        var a = await Connect(engine, cap, 1);

        await Send(engine, new { type = "introspect", id = 2, connectionId = a, send = false });

        Assert.Empty(cap.Of("schema"));
        Assert.Contains(cap.Of("ok"), e => e.GetProperty("replyTo").GetInt32() == 2);
    }

    [Fact]
    public async Task Introspect_sends_the_schema_by_default()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new Provider()], cap);
        var a = await Connect(engine, cap, 1);
        await Send(engine, new { type = "introspect", id = 2, connectionId = a });
        Assert.Single(cap.Of("schema"));
    }

    // ---- finished queries are released ----

    [Fact]
    public async Task A_drained_query_is_released_and_fetching_it_again_does_nothing()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new Provider()], cap);
        var a = await Connect(engine, cap, 1);
        await Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select" });
        var queryId = cap.Of("columns").Single().GetProperty("queryId").GetInt32();

        for (var i = 0; i < 20 && cap.Of("complete").Count == 0; i++) await Send(engine, new { type = "fetchBatch", queryId });
        Assert.Single(cap.Of("complete"));

        var before = cap.Count;
        await Send(engine, new { type = "fetchBatch", queryId }); // after it was released
        Assert.Equal(before, cap.Count);
    }

    [Fact]
    public async Task A_statement_replaced_by_the_next_one_is_released()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new Provider()], cap);
        var a = await Connect(engine, cap, 1);
        await Send(engine, new { type = "execute", id = 2, connectionId = a, sql = "select" });
        var first = cap.Of("columns").Single().GetProperty("queryId").GetInt32();
        await Send(engine, new { type = "execute", id = 3, connectionId = a, sql = "select" }); // replaces it, unfetched

        var before = cap.Count;
        await Send(engine, new { type = "fetchBatch", queryId = first });
        Assert.Equal(before, cap.Count);
    }
}
