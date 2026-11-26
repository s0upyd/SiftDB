using System.Data.Common;
using System.Runtime.Loader;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.DynamoDB.Tests;

/// <summary>
/// The provider against DynamoDB Local (design section 14, F19). Needs SIFTDB_DYNAMO_ENDPOINT, e.g.
/// http://localhost:8000 (locally G:\DynamoDB\start.cmd; in CI a service container). Each run makes tables with a
/// name of its own and drops them at the end.
/// </summary>
public sealed class DynamoLocalTests : IAsyncLifetime
{
    private const string Region = "eu-west-2";
    private readonly string? _endpoint = Environment.GetEnvironmentVariable("SIFTDB_DYNAMO_ENDPOINT");
    private readonly string _orders = "orders_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _big = "big-" + Guid.NewGuid().ToString("N")[..8]; // a name that must be quoted
    private readonly DynamoProvider _p = new();
    private AmazonDynamoDBClient? _admin;

    private void Require() => Skip.If(string.IsNullOrEmpty(_endpoint), "SIFTDB_DYNAMO_ENDPOINT not set");

    private string ConnectionString(string? secret = "local") => _p.BuildConnectionString(new Dictionary<string, string>
        { ["auth"] = "keys", ["accessKeyId"] = "local", ["region"] = Region, ["endpoint"] = _endpoint! }, secret);

    private Task<IProviderConnection> Open() => _p.OpenAsync(ConnectionString(), ConnectionOptions.Default, CancellationToken.None);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_endpoint)) return;
        _admin = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"),
            new AmazonDynamoDBConfig { ServiceURL = _endpoint, AuthenticationRegion = Region });
        await _admin.CreateTableAsync(new CreateTableRequest
        {
            TableName = _orders, BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("pk", ScalarAttributeType.S), new("sk", ScalarAttributeType.N), new("customer", ScalarAttributeType.S)],
            KeySchema = [new("pk", KeyType.HASH), new("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes = [new GlobalSecondaryIndex
            {
                IndexName = "ByCustomer", KeySchema = [new("customer", KeyType.HASH)], Projection = new Projection { ProjectionType = ProjectionType.ALL },
            }],
        });
        await _admin.CreateTableAsync(new CreateTableRequest
        {
            TableName = _big, BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("id", ScalarAttributeType.S)], KeySchema = [new("id", KeyType.HASH)],
        });
        // Attributes in a different order per item, a map, a list, a set, a boolean, a NULL and binary.
        foreach (var statement in new[]
        {
            $$"""INSERT INTO "{{_orders}}" VALUE {'total': 12.5, 'pk': 'c#1', 'sk': 1, 'customer': 'ann', 'paid': true}""",
            $$"""INSERT INTO "{{_orders}}" VALUE {'pk': 'c#1', 'sk': 2, 'customer': 'ann', 'lines': [1, 'two'], 'meta': {'via': 'web', 'n': 3}, 'note': null}""",
            $$"""INSERT INTO "{{_orders}}" VALUE {'pk': 'c#2', 'sk': 1, 'customer': 'bob', 'tags': <<'a', 'b'>>}""",
        })
            await _admin.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement });
    }

    public async Task DisposeAsync()
    {
        if (_admin is null) return;
        foreach (var t in new[] { _orders, _big })
            try { await _admin.DeleteTableAsync(t); } catch (ResourceNotFoundException) { }
        _admin.Dispose();
    }

    private static async Task<(List<string> Columns, List<string?[]> Rows)> ReadAll(DbDataReader r)
    {
        var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var rows = new List<string?[]>();
        while (await r.ReadAsync())
            rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetString(i)).ToArray());
        return (columns, rows);
    }

    [SkippableFact]
    public async Task Select_puts_the_keys_first_then_attributes_in_the_order_first_seen()
    {
        Require();
        await using var conn = await Open();
        await using var r = await conn.ExecuteReaderAsync($"SELECT * FROM \"{_orders}\" WHERE pk = 'c#1'", CancellationToken.None);
        var (columns, rows) = await ReadAll(r);
        Assert.Equal(["pk", "sk"], columns.Take(2));
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(DynamoReader.OtherColumn, columns); // one page: every attribute has its own column
        string? Cell(int row, string column) => rows[row][columns.IndexOf(column)];
        var first = rows.FindIndex(x => x[1] == "1");
        var second = 1 - first;
        Assert.Equal("12.5", Cell(first, "total"));
        Assert.Equal("true", Cell(first, "paid"));
        Assert.Null(Cell(first, "lines"));                         // not on this item
        Assert.Equal("[1,\"two\"]", Cell(second, "lines"));        // a list as JSON
        Assert.Contains("\"via\":\"web\"", Cell(second, "meta"));   // a map as JSON
        Assert.Null(Cell(second, "note"));                         // NULL is NULL
        Assert.Equal("N", r.GetDataTypeName(1));                   // sk is a number, for styling
    }

    [SkippableFact]
    public async Task A_select_that_names_its_attributes_keeps_their_order()
    {
        Require();
        await using var conn = await Open();
        await using var r = await conn.ExecuteReaderAsync($"select customer, \"total\", pk from \"{_orders}\" where pk = 'c#1'", CancellationToken.None);
        var (columns, rows) = await ReadAll(r);
        Assert.Equal(["customer", "total", "pk"], columns);
        Assert.Equal(2, rows.Count);
    }

    [SkippableFact]
    public async Task Sets_show_as_json_arrays()
    {
        Require();
        await using var conn = await Open();
        await using var r = await conn.ExecuteReaderAsync($"select tags from \"{_orders}\" where pk = 'c#2'", CancellationToken.None);
        var (_, rows) = await ReadAll(r);
        Assert.Equal("[\"a\",\"b\"]", Assert.Single(rows)[0]);
    }

    [SkippableFact]
    public async Task An_empty_select_still_has_the_tables_keys_as_columns()
    {
        Require();
        await using var conn = await Open();
        await using var r = await conn.ExecuteReaderAsync($"select * from \"{_orders}\" where pk = 'nobody'", CancellationToken.None);
        var (columns, rows) = await ReadAll(r);
        Assert.Equal(["pk", "sk"], columns);
        Assert.Empty(rows);
    }

    [SkippableFact]
    public async Task Writes_run_and_commit_on_their_own()
    {
        Require();
        await using var conn = await Open();
        await using (var w = await conn.ExecuteReaderAsync($"UPDATE \"{_orders}\" SET total = 99 WHERE pk = 'c#2' AND sk = 1", CancellationToken.None))
        {
            Assert.Equal(0, w.FieldCount);
            Assert.False(await w.ReadAsync());
        }
        Assert.Equal(TransactionState.None, (await conn.Transactions.GetStateAsync(CancellationToken.None)).State);
        await using var r = await conn.ExecuteReaderAsync($"select total from \"{_orders}\" where pk = 'c#2' and sk = 1", CancellationToken.None);
        Assert.Equal("99", Assert.Single((await ReadAll(r)).Rows)[0]);
    }

    [SkippableFact]
    public async Task DynamoDBs_own_error_is_reported()
    {
        Require();
        await using var conn = await Open();
        var ex = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(() => conn.ExecuteReaderAsync("select * from where", CancellationToken.None));
        Assert.False(string.IsNullOrEmpty(ex.Message));
    }

    [SkippableFact]
    public async Task There_are_no_transactions_to_begin()
    {
        Require();
        await using var conn = await Open();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => conn.Transactions.BeginAsync(CancellationToken.None));
        Assert.Contains("commits it straight away", ex.Message);
    }

    [SkippableFact]
    public async Task Rows_stream_page_by_page_and_later_attributes_go_into_other()
    {
        Require();
        // About 1.3 MB, so a scan takes two pages (DynamoDB pages at 1 MB).
        var blob = new string('x', 4000);
        for (var batch = 0; batch < 13; batch++)
            await _admin!.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [_big] = Enumerable.Range(batch * 25, 25).Select(i => new WriteRequest(new PutRequest(new()
                {
                    ["id"] = new AttributeValue { S = $"item{i:D4}" }, ["blob"] = new AttributeValue { S = blob },
                }))).ToList() },
            });
        // Find an item on the second page and give it an attribute nothing on the first page has.
        var first = await _admin!.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"select id from \"{_big}\"" });
        var firstIds = first.Items.Select(i => i["id"].S).ToHashSet();
        Assert.False(string.IsNullOrEmpty(first.NextToken), "expected the scan to take more than one page");
        var all = new List<string>(firstIds);
        string? token = first.NextToken;
        while (!string.IsNullOrEmpty(token))
        {
            var page = await _admin.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"select id from \"{_big}\"", NextToken = token });
            all.AddRange(page.Items.Select(i => i["id"].S));
            token = page.NextToken;
        }
        var late = all.First(id => !firstIds.Contains(id));
        await _admin.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"update \"{_big}\" set late = 'yes' where id = '{late}'" });

        await using var conn = await Open();
        await using var r = await conn.ExecuteReaderAsync($"select * from \"{_big}\"", CancellationToken.None);
        var (columns, rows) = await ReadAll(r);
        Assert.Equal(325, rows.Count);
        Assert.Equal(["id", "blob", DynamoReader.OtherColumn], columns);
        var lateRow = rows.Single(x => x[0] == late);
        Assert.Equal("{\"late\":\"yes\"}", lateRow[2]);
        Assert.All(rows.Where(x => x[0] != late), x => Assert.Null(x[2]));
    }

    [SkippableFact]
    public async Task The_schema_is_the_region_with_each_tables_keys_and_indexes()
    {
        Require();
        await using var conn = await Open();
        var schema = await conn.IntrospectAsync(CancellationToken.None);
        var region = Assert.Single(schema.Schemas);
        Assert.Equal(Region, region.Name);
        var orders = region.Relations.Single(r => r.Name == _orders);
        Assert.Equal(["pk", "sk"], orders.PrimaryKey);
        Assert.Equal(["pk", "sk", "customer"], orders.Columns.Select(c => c.Name));
        Assert.Equal("N", orders.Columns[1].DataType);
        Assert.False(orders.Columns[0].IsNullable);
        Assert.True(orders.Columns[2].IsNullable); // an index key need not be on every item
        var index = Assert.Single(orders.Indexes);
        Assert.Equal("ByCustomer", index.Name);
        Assert.Equal(["customer"], index.Columns);
    }

    [SkippableFact]
    public async Task A_tables_details_have_its_count_key_and_indexes()
    {
        Require();
        await using var conn = await Open();
        var d = await conn.DescribeRelationAsync(Region, _orders, CancellationToken.None);
        Assert.NotNull(d);
        Assert.NotNull(d!.EstimatedRows);
        var key = Assert.Single(d.Keys);
        Assert.Equal(["pk", "sk"], key.Columns);
        Assert.Contains("partition key", key.Definition);
        var index = Assert.Single(d.Indexes);
        Assert.Equal("global", index.Method);
        Assert.Contains("projecting all attributes", index.Definition);
        Assert.Equal("Partition key", d.Columns[0].Comment);
        Assert.Null(await conn.DescribeRelationAsync(Region, "no_such_table_" + Guid.NewGuid().ToString("N"), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Loaded_as_a_plugin_it_connects_and_reads()
    {
        Require();
        var result = ProviderLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "providers"));
        var p = Assert.Single(result.Providers);
        Assert.Equal("dynamodb", p.Id);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(p.GetType().Assembly));
        await using var conn = await p.OpenAsync(ConnectionString(), ConnectionOptions.Default, CancellationToken.None);
        await using var r = await conn.ExecuteReaderAsync($"select pk from \"{_orders}\" where pk = 'c#2'", CancellationToken.None);
        Assert.True(await r.ReadAsync());
        Assert.Equal("c#2", r.GetString(0));
    }

    [SkippableFact]
    public async Task Through_the_engine_a_saved_connection_asks_for_the_key_then_connects()
    {
        Require();
        var dir = Directory.CreateTempSubdirectory("siftdb-dynamo-");
        try
        {
            var events = new List<string>();
            var store = new ConnectionStore(Path.Combine(dir.FullName, "connections.json"), new MemorySecrets());
            await using var engine = new EngineHost([_p], new Output(events), store);
            await engine.HandleCommandAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "saveConnection", id = 1,
                connection = new { name = "local", provider = "dynamodb", fields = new { auth = "keys", accessKeyId = "local", region = Region, endpoint = _endpoint }, rememberPassword = false, readOnly = false },
            }));
            var saved = Reply(events, 1).GetProperty("savedId").GetString();

            await engine.HandleCommandAsync($$"""{"type":"connect","id":2,"savedId":"{{saved}}"}""");
            var asked = Reply(events, 2);
            Assert.Equal("passwordNeeded", asked.GetProperty("type").GetString());
            Assert.Equal("Secret access key", asked.GetProperty("label").GetString());

            await engine.HandleCommandAsync($$"""{"type":"connect","id":3,"savedId":"{{saved}}","password":"local"}""");
            Assert.Equal("connected", Reply(events, 3).GetProperty("type").GetString());
        }
        finally { dir.Delete(true); }
    }

    [SkippableFact]
    public async Task Through_the_engine_names_are_written_without_the_region_and_there_are_no_transactions()
    {
        Require();
        var events = new List<string>();
        await using var engine = new EngineHost([_p], new Output(events));
        await engine.HandleCommandAsync(System.Text.Json.JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "dynamodb", connectionString = ConnectionString() }));
        var connected = Reply(events, 1);
        Assert.False(connected.GetProperty("transactions").GetBoolean());
        Assert.False(connected.GetProperty("autoTransaction").GetBoolean());
        var conn = connected.GetProperty("connectionId").GetInt32();

        await engine.HandleCommandAsync($$"""{"type":"introspect","id":2,"connectionId":{{conn}}}""");
        Assert.False(Reply(events, 2).GetProperty("writesSchemaNames").GetBoolean());

        // "Select top 100 rows": no region in front, no LIMIT (PartiQL has none), the name quoted as it must be.
        await engine.HandleCommandAsync(System.Text.Json.JsonSerializer.Serialize(new
            { type = "scriptObject", id = 3, connectionId = conn, kind = "relation", schema = Region, name = _big, script = "top" }));
        Assert.Equal($"select * from \"{_big}\";", Reply(events, 3).GetProperty("sql").GetString());

        // Completion after FROM offers the table alone, and no region to type a dot after.
        var prefix = _orders[..7];
        await engine.HandleCommandAsync(System.Text.Json.JsonSerializer.Serialize(new
            { type = "complete", id = 4, connectionId = conn, text = $"select * from {prefix}", offset = 14 + prefix.Length }));
        var items = Reply(events, 4).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("insertText").GetString() == _orders);
        Assert.DoesNotContain(items, i => i.GetProperty("insertText").GetString()!.Contains(Region));

        // A name typed in the wrong case is a different name here (case-sensitive): it is flagged with the fix, as on
        // Postgres, and nothing ever adds the region in front of it.
        var typed = $"select * from {_orders.ToUpperInvariant()} where pk = 'c#1'";
        await engine.HandleCommandAsync(System.Text.Json.JsonSerializer.Serialize(new
            { type = "analyzeSheet", id = 6, connectionId = conn, text = typed, selectionStart = 3, selectionEnd = 3, mode = "assisted" }));
        var analysis = Reply(events, 6);
        Assert.Equal(typed, analysis.GetProperty("pick").GetProperty("sql").GetString());
        var issue = Assert.Single(analysis.GetProperty("issues").EnumerateArray());
        Assert.Contains("differs in case", issue.GetProperty("message").GetString());
        Assert.Equal(_orders, issue.GetProperty("fixText").GetString());

        // Begin is refused with the reason.
        await engine.HandleCommandAsync($$"""{"type":"transaction","id":5,"connectionId":{{conn}},"action":"begin"}""");
        Assert.Contains("no transaction to begin", Reply(events, 5).GetProperty("message").GetString());
    }

    private static System.Text.Json.JsonElement Reply(List<string> events, int id) =>
        events.Select(e => System.Text.Json.JsonDocument.Parse(e).RootElement.Clone())
            .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);

    private sealed class Output(List<string> events) : IEngineOutput
    {
        public void SendEvent(string json) { lock (events) events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string id) => _values.GetValueOrDefault(id);
        public void Set(string id, string secret) => _values[id] = secret;
        public void Remove(string id) => _values.Remove(id);
    }
}
