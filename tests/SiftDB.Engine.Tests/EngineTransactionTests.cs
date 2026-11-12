using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>The transaction flow through the real EngineHost, with a scripted provider so each state can be forced.</summary>
public class EngineTransactionTests
{
    private static readonly DateTimeOffset Started = new(2026, 5, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A statement that returns no rows but reports how many it affected (INSERT/UPDATE/DELETE).</summary>
    private sealed class NoRowsReader(int affected) : DbDataReader
    {
        public override int RecordsAffected => affected;
        public override int FieldCount => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override bool Read() => false;
        public override bool NextResult() => false;
        public override object this[int ordinal] => throw new NotSupportedException();
        public override object this[string name] => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override string GetName(int ordinal) => throw new NotSupportedException();
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override string GetString(int ordinal) => throw new NotSupportedException();
        public override object GetValue(int ordinal) => throw new NotSupportedException();
        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override bool IsDBNull(int ordinal) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }

    /// <summary>The "server": SQL text drives it. begin/commit/rollback, "update" (writes 3 rows), "boom" (errors).</summary>
    private sealed class Server : ITransactionMonitor
    {
        public TransactionState State = TransactionState.None;
        public bool ProbeFails;
        public bool CanDetectWrites => true;

        public Task BeginAsync(CancellationToken ct) { State = TransactionState.Open; return Task.CompletedTask; }
        public Task CommitAsync(CancellationToken ct) { State = TransactionState.None; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) { State = TransactionState.None; return Task.CompletedTask; }

        public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct)
        {
            if (ProbeFails) throw new InvalidOperationException("probe failed");
            return Task.FromResult(new TransactionSnapshot(State, State is TransactionState.Open or TransactionState.Changed ? Started : null));
        }

        public DbDataReader Run(string sql)
        {
            switch (sql)
            {
                case "begin": State = TransactionState.Open; return new NoRowsReader(-1);
                case "commit": case "rollback": State = TransactionState.None; return new NoRowsReader(-1);
                case "update":
                    if (State == TransactionState.Open) State = TransactionState.Changed;
                    return new NoRowsReader(3);
                case "boom":
                    if (State is TransactionState.Open or TransactionState.Changed) State = TransactionState.Failed;
                    throw new InvalidOperationException("boom");
                default:
                    var t = new DataTable();
                    t.Columns.Add("x", typeof(int));
                    t.Rows.Add(1);
                    return t.CreateDataReader();
            }
        }
    }

    private sealed class Provider(Server server) : IDatabaseProvider
    {
        public string Id => "fake";
        public int ContractVersion => ProviderContract.CurrentVersion;
        public string DisplayName => Id;
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) => throw new NotSupportedException();
        public ParsedConnection ParseConnectionString(string connectionString) => throw new NotSupportedException();
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;
        public string? CheckReadOnly(string sql) => null;
        public bool CannotRunInTransaction(string sql) => false;
        public GenericType GenericTypeOf(string dataTypeName) => GenericType.Text;
        public ISqlParser Parser => throw new NotSupportedException();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();
        public Task<IProviderConnection> OpenAsync(string cs, ConnectionOptions options, CancellationToken ct) => Task.FromResult<IProviderConnection>(new Conn(server));

        private sealed class Conn(Server server) : IProviderConnection
        {
            public ITransactionMonitor Transactions => server;
            public Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct) => Task.FromResult(server.Run(sql));
            public Task<DatabaseSchema> IntrospectAsync(CancellationToken ct) => Task.FromResult(new DatabaseSchema([]));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Capture : IEngineOutput
    {
        private readonly object _gate = new();
        public readonly List<string> Events = new();
        public void SendEvent(string json) { lock (_gate) Events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch b) { }
        public List<JsonElement> Of(string type)
        {
            lock (_gate)
                return Events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
                    .Where(e => e.GetProperty("type").GetString() == type).ToList();
        }
    }

    private static async Task<(EngineHost Engine, Capture Cap, Server Server)> Connected()
    {
        var cap = new Capture();
        var server = new Server();
        var engine = new EngineHost([new Provider(server)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"fake","connectionString":"x"}""");
        return (engine, cap, server);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    /// <summary>Runs one statement the way the UI does: execute, then fetch until the query completes.</summary>
    private static async Task Run(EngineHost engine, Capture cap, string sql, int queryId)
    {
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = 10 + queryId, connectionId = 1, sql }));
        if (cap.Of("error").Any(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == 10 + queryId)) return;
        for (var i = 0; i < 20 && !cap.Of("complete").Any(e => e.GetProperty("queryId").GetInt32() == queryId); i++)
            await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "fetchBatch", queryId }));
        await WaitFor(() => cap.Of("transaction").Count > 0, "the transaction event after the statement");
    }

    private static JsonElement Last(Capture cap) => cap.Of("transaction").Last();

    [Fact]
    public async Task Connecting_reports_that_there_is_no_transaction()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        var e = Last(cap);
        Assert.Equal("none", e.GetProperty("state").GetString());
        Assert.Equal(0, e.GetProperty("statements").GetInt32());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task The_begin_button_opens_a_transaction_and_replies_ok()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await engine.HandleCommandAsync("""{"type":"transaction","id":5,"connectionId":1,"action":"begin"}""");

        Assert.Equal("open", Last(cap).GetProperty("state").GetString());
        Assert.Equal(Started, Last(cap).GetProperty("startedAt").GetDateTimeOffset());
        Assert.Contains(cap.Of("ok"), e => e.GetProperty("replyTo").GetInt32() == 5);
    }

    [Fact]
    public async Task A_write_turns_it_yellow_and_the_summary_counts_statements_and_rows()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await engine.HandleCommandAsync("""{"type":"transaction","id":5,"connectionId":1,"action":"begin"}""");
        await Run(engine, cap, "select", 1);
        var afterSelect = Last(cap);
        Assert.Equal("open", afterSelect.GetProperty("state").GetString());
        Assert.Equal(1, afterSelect.GetProperty("statements").GetInt32());

        await Run(engine, cap, "update", 2);
        await WaitFor(() => Last(cap).GetProperty("state").GetString() == "changed", "changed state");
        var e = Last(cap);
        Assert.Equal(2, e.GetProperty("statements").GetInt32());
        Assert.Equal(3, e.GetProperty("rowsChanged").GetInt64());
    }

    [Fact]
    public async Task A_typed_BEGIN_opens_it_exactly_like_the_button_without_being_counted()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await Run(engine, cap, "begin", 1);
        await WaitFor(() => Last(cap).GetProperty("state").GetString() == "open", "open state");
        Assert.Equal(0, Last(cap).GetProperty("statements").GetInt32());
    }

    [Fact]
    public async Task Commit_and_rollback_clear_the_summary()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await engine.HandleCommandAsync("""{"type":"transaction","id":5,"connectionId":1,"action":"begin"}""");
        await Run(engine, cap, "update", 1);
        await WaitFor(() => Last(cap).GetProperty("state").GetString() == "changed", "changed state");

        await engine.HandleCommandAsync("""{"type":"transaction","id":6,"connectionId":1,"action":"commit"}""");
        var e = Last(cap);
        Assert.Equal("none", e.GetProperty("state").GetString());
        Assert.Equal(0, e.GetProperty("statements").GetInt32());
        Assert.Equal(0, e.GetProperty("rowsChanged").GetInt64());
    }

    [Fact]
    public async Task An_error_inside_a_transaction_turns_it_red_even_though_no_rows_streamed()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await engine.HandleCommandAsync("""{"type":"transaction","id":5,"connectionId":1,"action":"begin"}""");
        await Run(engine, cap, "boom", 1);

        Assert.Equal("failed", Last(cap).GetProperty("state").GetString());
        Assert.Contains(cap.Of("error"), e => e.GetProperty("message").GetString() == "boom");

        await engine.HandleCommandAsync("""{"type":"transaction","id":6,"connectionId":1,"action":"rollback"}""");
        Assert.Equal("none", Last(cap).GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_complete_event_carries_rows_affected_for_writes()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await Run(engine, cap, "update", 1);

        var complete = cap.Of("complete").Single(e => e.GetProperty("queryId").GetInt32() == 1);
        Assert.Equal(3, complete.GetProperty("rowsAffected").GetInt64());
        // What a real driver reports for a SELECT is checked against Postgres (TransactionFlowTests), not faked here.
    }

    [Fact]
    public async Task A_negative_count_means_the_statement_has_none_and_is_sent_as_null()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await Run(engine, cap, "begin", 1); // the fake reports -1 for BEGIN, like a real driver does for statements with no count

        var complete = cap.Of("complete").Single(e => e.GetProperty("queryId").GetInt32() == 1);
        Assert.Equal(JsonValueKind.Null, complete.GetProperty("rowsAffected").ValueKind);
    }

    [Fact]
    public async Task A_new_statement_replaces_a_running_one_without_deadlocking_on_the_session()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        // Start a statement and walk away without fetching it, then run another on the same session.
        await engine.HandleCommandAsync("""{"type":"execute","id":20,"connectionId":1,"sql":"select"}""");
        var second = engine.HandleCommandAsync("""{"type":"execute","id":21,"connectionId":1,"sql":"select"}""");

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(3000)));
        Assert.Equal(2, cap.Of("columns").Count);
    }

    [Fact]
    public async Task A_failing_probe_is_reported_and_does_not_wedge_the_session()
    {
        var (engine, cap, server) = await Connected();
        await using var _ = engine;
        server.ProbeFails = true;
        await engine.HandleCommandAsync("""{"type":"transaction","id":5,"connectionId":1,"action":"begin"}""");
        Assert.Contains(cap.Of("transactionUnknown"), e => e.GetProperty("message").GetString() == "probe failed");

        server.ProbeFails = false;
        await Run(engine, cap, "select", 1); // would hang if the lock had leaked
        Assert.NotEmpty(cap.Of("complete"));
    }

    [Fact]
    public async Task An_unknown_action_or_connection_is_an_error()
    {
        var (engine, cap, _) = await Connected();
        await using var _ = engine;
        await engine.HandleCommandAsync("""{"type":"transaction","id":7,"connectionId":1,"action":"explode"}""");
        await engine.HandleCommandAsync("""{"type":"transaction","id":8,"connectionId":99,"action":"begin"}""");
        var errors = cap.Of("error").Select(e => e.GetProperty("replyTo").GetInt32()).ToList();
        Assert.Contains(7, errors);
        Assert.Contains(8, errors);
    }
}
