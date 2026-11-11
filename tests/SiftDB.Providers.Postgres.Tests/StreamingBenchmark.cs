using System.Diagnostics;
using System.Text.Json;
using SiftDB.Engine;
using Xunit.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Engine-only streaming measurement against a real server (no UI, no WebView2).
/// Skipped unless SIFTDB_SPIKE_CONN is set. Run with: dotnet test --filter StreamingBenchmark -c Release --logger "console;verbosity=detailed"
/// </summary>
public class StreamingBenchmark(ITestOutputHelper output)
{
    private sealed class Sink : IEngineOutput
    {
        public readonly List<string> Events = new();
        public int Batches;
        public long Bytes;
        public void SendEvent(string json) { lock (Events) Events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch b) { Batches++; Bytes += b.Data.Length; }
    }

    [SkippableFact]
    public async Task Stream_one_million_rows()
    {
        var cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
        Skip.If(string.IsNullOrEmpty(cs), "SIFTDB_SPIKE_CONN not set");

        const string sql = "select i, i::bigint * 3 as big, random() as r, md5(i::text) as hash from generate_series(1, 1000000) as i";
        for (var run = 1; run <= 3; run++)
        {
            var sink = new Sink();
            await using var engine = new EngineHost([new PostgresProvider()], sink);
            await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "postgres", connectionString = cs }));
            var sw = Stopwatch.StartNew();
            await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = 2, connectionId = 1, sql }));

            string? complete = null;
            while (complete is null)
            {
                await engine.HandleCommandAsync("""{"type":"fetchBatch","queryId":1}""");
                lock (sink.Events) complete = sink.Events.FirstOrDefault(e => e.Contains("\"complete\"") || e.Contains("\"error\""));
            }
            output.WriteLine($"run {run}: wall {sw.Elapsed.TotalMilliseconds:F0} ms, {sink.Batches} batches, {sink.Bytes / 1048576.0:F1} MB");
            output.WriteLine("        " + complete);
        }
    }
}
