using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Bridge;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

public class EngineTests
{
    private sealed class FakeProvider(int rows) : IDatabaseProvider
    {
        public string Id => "fake";
        public int ContractVersion => ProviderContract.CurrentVersion;
        public string DisplayName => Id;
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) => throw new NotSupportedException();
        public ParsedConnection ParseConnectionString(string connectionString) => throw new NotSupportedException();
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;
        public string? CheckReadOnly(string sql) => null;
        public ISqlParser Parser { get; } = new SemicolonParser();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();

        public Task<IProviderConnection> OpenAsync(string cs, ConnectionOptions options, CancellationToken ct) =>
            Task.FromResult<IProviderConnection>(new Conn(rows));

        /// <summary>Toy dialect: statements are separated by ';' (no quoting rules).</summary>
        private sealed class SemicolonParser : ISqlParser
        {
            public IReadOnlyList<SqlToken> Tokenize(string sql) => [];
            public SqlSyntaxError? Validate(string statement) => null;
            public IReadOnlyList<SqlRange> Split(string sql)
            {
                var list = new List<SqlRange>();
                var pos = 0;
                foreach (var chunk in sql.Split(';'))
                {
                    var lead = chunk.Length - chunk.TrimStart().Length;
                    var body = chunk.Trim();
                    if (body.Length > 0) list.Add(new SqlRange(pos + lead, body.Length));
                    pos += chunk.Length + 1;
                }
                return list;
            }
        }

        private sealed class Conn(int rows) : IProviderConnection
        {
            public Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
            {
                var t = new DataTable();
                t.Columns.Add("i", typeof(int));
                t.Columns.Add("big", typeof(long));
                t.Columns.Add("r", typeof(double));
                t.Columns.Add("s", typeof(string));
                for (var i = 0; i < rows; i++) t.Rows.Add(i, (long)i * 3, i / 2.0, i % 7 == 0 ? null : $"row é{i}");
                return Task.FromResult<DbDataReader>(t.CreateDataReader());
            }
            public ITransactionMonitor Transactions { get; } = new NullMonitor();
            public Task<DatabaseSchema> IntrospectAsync(CancellationToken ct) => Task.FromResult(new DatabaseSchema([
                new SchemaInfo("public", [
                    new RelationInfo("OrderItems", RelationKind.MaterializedView,
                        [new ColumnInfo("id", "integer", false, null)], ["id"], [], [])])]));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Capture : IEngineOutput
    {
        public List<string> Events = new();
        public List<EncodedBatch> Batches = new();
        public void SendEvent(string json) { lock (Events) Events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch b) { lock (Batches) Batches.Add(b); }
    }

    [Fact]
    public async Task Streams_all_rows_in_batches_and_completes()
    {
        const int rows = 45_000;
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(rows)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"fake","connectionString":"x"}""");
        await engine.HandleCommandAsync("""{"type":"execute","id":2,"connectionId":1,"sql":"select"}""");

        var completed = false;
        for (var i = 0; i < 1000 && !completed; i++)
        {
            await engine.HandleCommandAsync("""{"type":"fetchBatch","queryId":1}""");
            lock (cap.Events) completed = cap.Events.Any(e => e.Contains("\"complete\""));
        }

        Assert.True(completed);
        Assert.Equal(rows, cap.Batches.Sum(b => b.RowCount));
        Assert.Equal(Enumerable.Range(0, cap.Batches.Count), cap.Batches.OrderBy(b => b.Seq).Select(b => b.Seq));
        Assert.Contains(cap.Events, e => e.Contains("\"totalRows\":45000"));
    }

    [Fact]
    public async Task Batch_layout_roundtrips()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(10)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"fake","connectionString":"x"}""");
        await engine.HandleCommandAsync("""{"type":"execute","id":2,"connectionId":1,"sql":"select"}""");
        await engine.HandleCommandAsync("""{"type":"fetchBatch","queryId":1}""");

        var d = cap.Batches.Single().Data;
        Assert.Equal(RowBatchWriter.Magic, BinaryPrimitives.ReadUInt32LittleEndian(d));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)));

        uint Kind(int c) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12 + c * 20));
        int Off(int c) => (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16 + c * 20));
        Assert.Equal([1u, 2u, 3u, 0u], new[] { Kind(0), Kind(1), Kind(2), Kind(3) });
        Assert.All(new[] { 0, 1, 2, 3 }, c => Assert.Equal(0, Off(c) % 8));

        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Off(0) + 7 * 4)));
        Assert.Equal(21L, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(Off(1) + 7 * 8)));
        Assert.Equal(3.5, BitConverter.ToDouble(d, Off(2) + 7 * 8));

        var so = Off(3);
        int Str(int i) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(so + i * 4));
        var dataStart = so + 11 * 4;
        Assert.Equal("row é3", Encoding.UTF8.GetString(d, dataStart + Str(3), Str(4) - Str(3)));
        Assert.Equal(0, Str(1) - Str(0)); // row 0 is null -> empty
    }

    [Fact]
    public async Task Introspect_replies_with_a_camelCase_schema_event()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(1)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"fake","connectionString":"x"}""");
        await engine.HandleCommandAsync("""{"type":"introspect","id":7,"connectionId":1}""");

        using var doc = JsonDocument.Parse(cap.Events.Single(e => e.Contains("\"schema\"")));
        var root = doc.RootElement;
        Assert.Equal("schema", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("replyTo").GetInt32());
        Assert.Equal(1, root.GetProperty("connectionId").GetInt32());

        var rel = root.GetProperty("schema").GetProperty("schemas")[0].GetProperty("relations")[0];
        Assert.Equal("OrderItems", rel.GetProperty("name").GetString());
        Assert.Equal("materializedView", rel.GetProperty("kind").GetString());
        var col = rel.GetProperty("columns")[0];
        Assert.Equal("integer", col.GetProperty("dataType").GetString());
        Assert.False(col.GetProperty("isNullable").GetBoolean());
        Assert.Equal("id", rel.GetProperty("primaryKey")[0].GetString());
    }

    [Fact]
    public async Task AnalyzeSheet_returns_statement_ranges_and_the_pick()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(1)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"fake","connectionString":"x"}""");

        const string sheet = "select 1;\nselect 2;\n";
        // Cursor inside the second statement.
        var cursor = sheet.IndexOf("select 2", StringComparison.Ordinal) + 3;
        await engine.HandleCommandAsync(
            $$"""{"type":"analyzeSheet","id":5,"connectionId":1,"text":"select 1;\nselect 2;\n","selectionStart":{{cursor}},"selectionEnd":{{cursor}}}""");

        using var doc = JsonDocument.Parse(cap.Events.Single(e => e.Contains("sheetAnalysis")));
        var root = doc.RootElement;
        Assert.Equal(5, root.GetProperty("replyTo").GetInt32());

        var statements = root.GetProperty("statements").EnumerateArray().ToArray();
        Assert.Equal(2, statements.Length);
        Assert.Equal(0, statements[0].GetProperty("start").GetInt32());
        Assert.Equal(8, statements[0].GetProperty("length").GetInt32());

        var pick = root.GetProperty("pick");
        Assert.Equal("cursorStatement", pick.GetProperty("reason").GetString());
        Assert.Equal(sheet.IndexOf("select 2", StringComparison.Ordinal), pick.GetProperty("start").GetInt32());
        Assert.Equal(8, pick.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task AnalyzeSheet_reports_none_when_there_is_nothing_to_run()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(1)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"fake","connectionString":"x"}""");
        await engine.HandleCommandAsync("""{"type":"analyzeSheet","id":2,"connectionId":1,"text":"  \n","selectionStart":1,"selectionEnd":1}""");

        using var doc = JsonDocument.Parse(cap.Events.Single(e => e.Contains("sheetAnalysis")));
        Assert.Equal("none", doc.RootElement.GetProperty("pick").GetProperty("reason").GetString());
        Assert.Empty(doc.RootElement.GetProperty("statements").EnumerateArray());
    }

    [Fact]
    public async Task AnalyzeSheet_on_an_unknown_connection_replies_with_an_error()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(1)], cap);
        await engine.HandleCommandAsync("""{"type":"analyzeSheet","id":4,"connectionId":42,"text":"x","selectionStart":0,"selectionEnd":0}""");
        Assert.Contains(cap.Events, e => e.Contains("\"error\"") && e.Contains("\"replyTo\":4"));
    }

    [Fact]
    public async Task Introspect_on_an_unknown_connection_replies_with_an_error()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new FakeProvider(1)], cap);
        await engine.HandleCommandAsync("""{"type":"introspect","id":3,"connectionId":99}""");
        Assert.Contains(cap.Events, e => e.Contains("\"error\"") && e.Contains("\"replyTo\":3"));
    }

    [Fact]
    public async Task Rejects_provider_with_wrong_contract_version()
    {
        var bad = new BadProvider();
        Assert.Throws<InvalidOperationException>(() => new EngineHost([bad], new Capture()));
        await Task.CompletedTask;
    }

    private sealed class BadProvider : IDatabaseProvider
    {
        public string Id => "bad";
        public int ContractVersion => 99;
        public string DisplayName => Id;
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) => throw new NotSupportedException();
        public ParsedConnection ParseConnectionString(string connectionString) => throw new NotSupportedException();
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;
        public string? CheckReadOnly(string sql) => null;
        public ISqlParser Parser => throw new NotSupportedException();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();

        public Task<IProviderConnection> OpenAsync(string c, ConnectionOptions options, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>A monitor for tests that do not care about transactions: always no transaction.</summary>
internal sealed class NullMonitor : ITransactionMonitor
{
    public bool CanDetectWrites => true;
    public Task BeginAsync(CancellationToken ct) => Task.CompletedTask;
    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct) =>
        Task.FromResult(new TransactionSnapshot(TransactionState.None, null));
}
