using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SiftDB.Engine;
using Xunit.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>NULLs from a real server, through the real Npgsql reader and the engine. Needs SIFTDB_SPIKE_CONN.</summary>
public class NullRoundTripTests(ITestOutputHelper output)
{
    private sealed class Sink : IEngineOutput
    {
        public readonly List<string> Events = new();
        public readonly List<EncodedBatch> Batches = new();
        public void SendEvent(string json) => Events.Add(json);
        public void SendBatch(int queryId, EncodedBatch b) => Batches.Add(b);
    }

    private static (uint Kind, int Off, int Len, int NullOff, int NullLen) Col(byte[] d, int c)
    {
        var e = 12 + c * 20;
        return (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e)), (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 8)), (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 12)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 16)));
    }

    private static bool IsNull(byte[] d, int c, int row)
    {
        var col = Col(d, c);
        return col.NullLen > 0 && (d[col.NullOff + (row >> 3)] & (1 << (row & 7))) != 0;
    }

    [SkippableFact]
    public async Task Real_server_NULLs_arrive_flagged_and_distinct_from_zero_and_empty()
    {
        var cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
        Skip.If(string.IsNullOrEmpty(cs), "SIFTDB_SPIKE_CONN not set");

        // Row 0 has values (including '' and 0), row 1 is all NULL, row 2 has values again.
        const string sql = """
            select * from (values
                (1::int,  'a'::text, 1.5::float8, 10::bigint),
                (null,    null,      null,        null),
                (0,       '',        0.0,         0)
            ) v(i, s, f, b)
            """;

        var sink = new Sink();
        await using var engine = new EngineHost([new PostgresProvider()], sink);
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "postgres", connectionString = cs }));
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = 2, connectionId = 1, sql }));
        await engine.HandleCommandAsync("""{"type":"fetchBatch","queryId":1}""");

        var d = sink.Batches.Single().Data;
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)));

        for (var c = 0; c < 4; c++)
        {
            Assert.False(IsNull(d, c, 0), $"col {c} row 0");
            Assert.True(IsNull(d, c, 1), $"col {c} row 1");
            Assert.False(IsNull(d, c, 2), $"col {c} row 2"); // 0 and '' are real values, not NULL
        }
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Col(d, 0).Off + 8)));

        // Hand the exact bytes to whoever wants to replay them (the UI check).
        output.WriteLine("BATCH:" + Convert.ToBase64String(d));
    }
}
