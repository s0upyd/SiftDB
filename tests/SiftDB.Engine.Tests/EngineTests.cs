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
        public Task<IProviderConnection> OpenAsync(string cs, CancellationToken ct) =>
            Task.FromResult<IProviderConnection>(new Conn(rows));

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

        uint Kind(int c) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12 + c * 12));
        int Off(int c) => (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16 + c * 12));
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
        public Task<IProviderConnection> OpenAsync(string c, CancellationToken ct) => throw new NotSupportedException();
    }
}
