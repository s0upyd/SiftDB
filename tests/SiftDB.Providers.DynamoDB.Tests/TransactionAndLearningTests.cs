using System.Data.Common;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.DynamoDB.Tests;

/// <summary>
/// DynamoDB transactions as SiftDB holds them (writes queued, one ExecuteTransaction at Commit), attributes learnt from
/// items, and the capacity note (design section 14, F19). Against DynamoDB Local, like DynamoLocalTests.
/// </summary>
public sealed class TransactionAndLearningTests : IAsyncLifetime
{
    private const string Region = "eu-west-2";
    private readonly string? _endpoint = Environment.GetEnvironmentVariable("SIFTDB_DYNAMO_ENDPOINT");
    private readonly string _t = "tx_" + Guid.NewGuid().ToString("N")[..8];
    private readonly DynamoProvider _p = new();
    private AmazonDynamoDBClient? _admin;

    private void Require() => Skip.If(string.IsNullOrEmpty(_endpoint), "SIFTDB_DYNAMO_ENDPOINT not set");

    private string ConnectionString() => _p.BuildConnectionString(new Dictionary<string, string>
        { ["auth"] = "keys", ["accessKeyId"] = "local", ["region"] = Region, ["endpoint"] = _endpoint! }, "local");

    private Task<IProviderConnection> Open() => _p.OpenAsync(ConnectionString(), ConnectionOptions.Default, CancellationToken.None);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_endpoint)) return;
        _admin = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), new AmazonDynamoDBConfig { ServiceURL = _endpoint, AuthenticationRegion = Region });
        await _admin.CreateTableAsync(new CreateTableRequest
        {
            TableName = _t, BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("id", ScalarAttributeType.S)], KeySchema = [new("id", KeyType.HASH)],
        });
        foreach (var s in new[]
        {
            $$"""INSERT INTO "{{_t}}" VALUE {'id': 'a', 'stock': 5, 'colour': 'red'}""",
            $$"""INSERT INTO "{{_t}}" VALUE {'id': 'b', 'stock': 9, 'size': 'L'}""",
        })
            await _admin.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = s });
    }

    public async Task DisposeAsync()
    {
        if (_admin is null) return;
        try { await _admin.DeleteTableAsync(_t); } catch (ResourceNotFoundException) { }
        _admin.Dispose();
    }

    private async Task<string?> Stock(string id) =>
        (await _admin!.GetItemAsync(_t, new() { ["id"] = new AttributeValue { S = id } })).Item?.GetValueOrDefault("stock")?.N;

    private static async Task Drain(DbDataReader r) { while (await r.ReadAsync()) { } }

    // ---- transactions --------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Writes_after_begin_are_queued_and_commit_sends_them_together()
    {
        Require();
        await using var conn = await Open();
        await conn.Transactions.BeginAsync(CancellationToken.None);
        Assert.Equal(TransactionState.Open, (await conn.Transactions.GetStateAsync(CancellationToken.None)).State);

        await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set stock = 4 where id = 'a'", CancellationToken.None))
        {
            await Drain(w);
            var note = Assert.IsAssignableFrom<IResultNote>(w);
            Assert.Equal("Queued", note.NoteHeadline);
            Assert.Contains("statement 1 of the transaction", note.NoteDetail);
        }
        await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set stock = 10 where id = 'b'", CancellationToken.None))
            Assert.Contains("statement 2 of the transaction", ((IResultNote)w).NoteDetail);
        Assert.Equal(TransactionState.Changed, (await conn.Transactions.GetStateAsync(CancellationToken.None)).State);
        Assert.Equal("5", await Stock("a")); // nothing sent yet

        // A SELECT runs now, and says it sees only what is committed.
        await using (var r = await conn.ExecuteReaderAsync($"select stock from \"{_t}\" where id = 'a'", CancellationToken.None))
        {
            Assert.True(await r.ReadAsync());
            Assert.Equal("5", r.GetString(0));
            await Drain(r);
            Assert.Contains("outside the transaction", ((IResultNote)r).NoteDetail);
        }

        await conn.Transactions.CommitAsync(CancellationToken.None);
        Assert.Equal(("4", "10"), (await Stock("a"), await Stock("b")));
        Assert.Equal(TransactionState.None, (await conn.Transactions.GetStateAsync(CancellationToken.None)).State);
        Assert.StartsWith("Committed 2 statements", ((IResultNote)conn.Transactions).NoteDetail);
    }

    [SkippableFact]
    public async Task Rollback_drops_the_queued_writes()
    {
        Require();
        await using var conn = await Open();
        await conn.Transactions.BeginAsync(CancellationToken.None);
        await using (var w = await conn.ExecuteReaderAsync($"delete from \"{_t}\" where id = 'a'", CancellationToken.None)) await Drain(w);
        await conn.Transactions.RollbackAsync(CancellationToken.None);
        Assert.Equal("5", await Stock("a"));
        Assert.Equal(TransactionState.None, (await conn.Transactions.GetStateAsync(CancellationToken.None)).State);
    }

    [SkippableFact]
    public async Task A_refused_commit_writes_nothing_and_says_which_statement_and_why()
    {
        Require();
        await using var conn = await Open();
        await conn.Transactions.BeginAsync(CancellationToken.None);
        await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set stock = 0 where id = 'b'", CancellationToken.None)) await Drain(w);
        // Its condition doesn't hold (stock is 5), so DynamoDB refuses the whole transaction.
        await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set stock = 1 where id = 'a' and stock = 999", CancellationToken.None)) await Drain(w);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Transactions.CommitAsync(CancellationToken.None));
        Assert.Contains("nothing was written", ex.Message);
        Assert.Contains("statement 2", ex.Message);
        Assert.Contains("ConditionalCheckFailed", ex.Message);
        Assert.Equal("9", await Stock("b")); // the first one wasn't applied either
        Assert.Equal(TransactionState.None, (await conn.Transactions.GetStateAsync(CancellationToken.None)).State);
    }

    [SkippableFact]
    public async Task Exists_conditions_are_queued_with_the_writes()
    {
        Require();
        await using var conn = await Open();
        await conn.Transactions.BeginAsync(CancellationToken.None);
        await using (var c = await conn.ExecuteReaderAsync($"exists(select * from \"{_t}\" where id = 'a' and stock = 5)", CancellationToken.None))
            Assert.Equal("Queued", ((IResultNote)c).NoteHeadline);
        await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set stock = 3 where id = 'b'", CancellationToken.None)) await Drain(w);
        await conn.Transactions.CommitAsync(CancellationToken.None);
        Assert.Equal("3", await Stock("b"));
    }

    [SkippableFact]
    public async Task A_transaction_holds_at_most_a_hundred_statements()
    {
        Require();
        await using var conn = await Open();
        await conn.Transactions.BeginAsync(CancellationToken.None);
        for (var i = 0; i < 100; i++)
            await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set n{i} = 1 where id = 'a'", CancellationToken.None)) await Drain(w);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.ExecuteReaderAsync($"update \"{_t}\" set x = 1 where id = 'a'", CancellationToken.None));
        Assert.Contains("at most 100 statements", ex.Message);
        await conn.Transactions.RollbackAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Without_begin_a_write_runs_at_once_and_says_done()
    {
        Require();
        await using var conn = await Open();
        await using (var w = await conn.ExecuteReaderAsync($"update \"{_t}\" set stock = 7 where id = 'a'", CancellationToken.None))
        {
            await Drain(w);
            Assert.Null(((IResultNote)w).NoteHeadline);
        }
        Assert.Equal("7", await Stock("a"));
    }

    [SkippableFact]
    public async Task Through_the_engine_a_queued_write_says_so_and_commit_says_what_it_did()
    {
        Require();
        var events = new List<string>();
        await using var engine = new EngineHost([_p], new Output(events));
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "dynamodb", connectionString = ConnectionString() }));
        var conn = Reply(events, 1).GetProperty("connectionId").GetInt32();
        await engine.HandleCommandAsync($$"""{"type":"transaction","id":2,"connectionId":{{conn}},"action":"begin"}""");
        Assert.Equal("ok", Reply(events, 2).GetProperty("type").GetString());

        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = 3, connectionId = conn, sql = $"update \"{_t}\" set stock = 2 where id = 'b'", allRowsChecked = true }));
        var queryId = Reply(events, 3).GetProperty("queryId").GetInt32();
        JsonElement? complete = null;
        for (var i = 0; i < 50 && complete is null; i++)
        {
            await engine.HandleCommandAsync($$"""{"type":"fetchBatch","queryId":{{queryId}}}""");
            lock (events) complete = events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
                .Cast<JsonElement?>().LastOrDefault(e => e!.Value.GetProperty("type").GetString() == "complete");
        }
        Assert.NotNull(complete);
        Assert.Equal("Queued", complete!.Value.GetProperty("noteHeadline").GetString());
        // The tab goes amber: the engine reads the state after the statement.
        lock (events) Assert.Contains(events, e => e.Contains("\"type\":\"transaction\"") && e.Contains("\"changed\""));

        await engine.HandleCommandAsync($$"""{"type":"transaction","id":4,"connectionId":{{conn}},"action":"commit"}""");
        Assert.StartsWith("Committed 1 statement", Reply(events, 4).GetProperty("note").GetString());
        Assert.Equal("2", await Stock("b"));
    }

    // ---- attributes learnt from items ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_select_teaches_the_schema_its_attributes_and_says_so()
    {
        Require();
        await using var conn = await Open();
        var schema = await conn.IntrospectAsync(CancellationToken.None);
        Assert.Equal(["id"], schema.Schemas[0].Relations.Single(r => r.Name == _t).Columns.Select(c => c.Name));

        var changed = 0;
        conn.SchemaChanged += () => Interlocked.Increment(ref changed);
        await using (var r = await conn.ExecuteReaderAsync($"select * from \"{_t}\"", CancellationToken.None)) await Drain(r);
        Assert.Equal(1, changed);

        var learnt = (await conn.IntrospectAsync(CancellationToken.None)).Schemas[0].Relations.Single(r => r.Name == _t).Columns;
        Assert.Equal("id", learnt[0].Name);
        Assert.Equal("stock", learnt[1].Name);                              // on both items: first
        Assert.Equal("N", learnt[1].DataType);
        Assert.True(learnt[1].IsNullable);                                   // seen, not declared
        Assert.Equal(["colour", "size"], learnt.Skip(2).Select(c => c.Name).Order());

        // The same rows again teach nothing new: no second reload.
        await using (var r = await conn.ExecuteReaderAsync($"select * from \"{_t}\"", CancellationToken.None)) await Drain(r);
        Assert.Equal(1, changed);
    }

    [SkippableFact]
    public async Task Writing_a_statement_about_a_table_samples_it_once()
    {
        Require();
        await using var conn = await Open();
        await conn.IntrospectAsync(CancellationToken.None); // the table must be known
        var learnt = new TaskCompletionSource();
        conn.SchemaChanged += () => learnt.TrySetResult();
        conn.StatementInView($"select * from \"{_t}\" where ");
        await learnt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var columns = (await conn.IntrospectAsync(CancellationToken.None)).Schemas[0].Relations.Single(r => r.Name == _t).Columns;
        Assert.Contains(columns, c => c.Name == "colour");

        // Learnt once for the account: a second connection (another sheet) knows too, without sampling again.
        await using var other = await Open();
        var again = 0;
        other.SchemaChanged += () => again++;
        var theirs = (await other.IntrospectAsync(CancellationToken.None)).Schemas[0].Relations.Single(r => r.Name == _t).Columns;
        Assert.Contains(theirs, c => c.Name == "colour");
        other.StatementInView($"select * from \"{_t}\"");
        await Task.Delay(300);
        Assert.Equal(0, again);
    }

    [SkippableFact]
    public async Task Only_from_results_means_no_sampling()
    {
        Require();
        _p.ApplySettings(new Dictionary<string, string> { [DynamoSettings.LearnKey] = DynamoSettings.ResultsOnly });
        await using var conn = await Open();
        await conn.IntrospectAsync(CancellationToken.None);
        var changed = 0;
        conn.SchemaChanged += () => changed++;
        conn.StatementInView($"select * from \"{_t}\"");
        await Task.Delay(500);
        Assert.Equal(0, changed);
    }

    [SkippableFact]
    public async Task A_learnt_attribute_says_how_often_it_was_seen()
    {
        Require();
        await using var conn = await Open();
        await using (var r = await conn.ExecuteReaderAsync($"select * from \"{_t}\"", CancellationToken.None)) await Drain(r);
        var d = await conn.DescribeRelationAsync(Region, _t, CancellationToken.None);
        Assert.Equal("Seen in 1 of the 2 items read so far (not declared: DynamoDB only declares keys)", d!.Columns.Single(c => c.Name == "colour").Comment);
        Assert.Equal("Partition key", d.Columns.Single(c => c.Name == "id").Comment);
    }

    [SkippableFact]
    public async Task Through_the_engine_an_attribute_the_schema_lacks_is_not_a_warning_and_learnt_ones_complete()
    {
        Require();
        var events = new List<string>();
        await using var engine = new EngineHost([_p], new Output(events));
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "dynamodb", connectionString = ConnectionString() }));
        var conn = Reply(events, 1).GetProperty("connectionId").GetInt32();
        await engine.HandleCommandAsync($$"""{"type":"introspect","id":2,"connectionId":{{conn}}}""");

        var text = $"select * from \"{_t}\" where nothing_known = 1";
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "analyzeSheet", id = 3, connectionId = conn, text, selectionStart = 3, selectionEnd = 3, mode = "assisted" }));
        Assert.DoesNotContain(Reply(events, 3).GetProperty("issues").EnumerateArray(), i => i.GetProperty("message").GetString()!.StartsWith("No column"));

        // That analysis was also the hint: the table is sampled, the engine is told, and after a reload completion knows.
        for (var i = 0; i < 100 && !Has(events, "schemaChanged"); i++) await Task.Delay(100);
        Assert.True(Has(events, "schemaChanged"));
        await engine.HandleCommandAsync($$"""{"type":"introspect","id":4,"connectionId":{{conn}}}""");
        var typed = $"select * from \"{_t}\" where co";
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "complete", id = 5, connectionId = conn, text = typed, offset = typed.Length }));
        Assert.Contains(Reply(events, 5).GetProperty("items").EnumerateArray(), i => i.GetProperty("insertText").GetString() == "colour");
    }

    // ---- capacity --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(4.5, "read", "4.5 read units")]
    [InlineData(1.0, "write", "1 write unit")]
    [InlineData(12.0, "read", "12 read units")]
    public void Capacity_reads_as_units(double units, string word, string said) => Assert.Equal(said, Capacity.Describe(units, word));

    [Fact]
    public async Task A_reader_adds_up_the_capacity_of_every_page()
    {
        var pages = new Queue<(List<Dictionary<string, AttributeValue>>, string?, double?)>([
            ([new() { ["id"] = new AttributeValue { S = "b" } }], null, 1.5),
        ]);
        await using var r = new DynamoReader(["id"], false, [new() { ["id"] = new AttributeValue { S = "a" } }], "next",
            (_, _) => Task.FromResult(pages.Dequeue()), units: 2.0, unitWord: "read");
        while (await r.ReadAsync()) { }
        Assert.Equal("3.5 read units", r.NoteDetail);
        Assert.Null(r.NoteHeadline);
    }

    [Fact]
    public void Without_reported_capacity_there_is_no_note()
    {
        using var r = new DynamoReader([], false, [], null, (_, _) => throw new InvalidOperationException());
        Assert.Null(r.NoteDetail);
    }

    private static bool Has(List<string> events, string type) { lock (events) return events.Any(e => e.Contains($"\"type\":\"{type}\"")); }

    private static JsonElement Reply(List<string> events, int id)
    {
        lock (events)
            return events.Select(e => JsonDocument.Parse(e).RootElement.Clone()).Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private sealed class Output(List<string> events) : IEngineOutput
    {
        public void SendEvent(string json) { lock (events) events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }
}
