using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// A read-only connection against the real server (design section 8): the server refuses writes, and switching read-only
/// off does not get through. Needs SIFTDB_SPIKE_CONN. Writes nothing (every write here is refused).
/// </summary>
public class ReadOnlySessionTests : IAsyncLifetime
{
    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private IProviderConnection? _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _conn = await new PostgresProvider().OpenAsync(_cs, new ConnectionOptions(ReadOnly: true), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_conn is not null) await _conn.DisposeAsync();
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task<string?> Scalar(string sql)
    {
        await using var r = await _conn!.ExecuteReaderAsync(sql, CancellationToken.None);
        string? value = null;
        while (await r.ReadAsync()) value ??= r.IsDBNull(0) ? null : r.GetValue(0).ToString();
        return value;
    }

    [SkippableFact]
    public async Task The_server_refuses_writes_even_temp_tables()
    {
        Require();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Scalar("create temp table ro_probe (x int)"));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, ex.SqlState);
    }

    [SkippableFact]
    public async Task Reset_all_and_discard_all_fall_back_to_read_only()
    {
        Require();
        Assert.Equal("on", await Scalar("show default_transaction_read_only"));
        await Scalar("reset all");
        Assert.Equal("on", await Scalar("show default_transaction_read_only"));
        await Scalar("discard all");
        Assert.Equal("on", await Scalar("show default_transaction_read_only"));
        await Scalar("set default_transaction_read_only to default");
        Assert.Equal("on", await Scalar("show default_transaction_read_only"));
    }

    [SkippableFact]
    public async Task A_typed_begin_is_read_only_too()
    {
        Require();
        await Scalar("begin");
        Assert.Equal("on", await Scalar("show transaction_read_only"));
        await Scalar("rollback");
    }

    [SkippableFact]
    public async Task Introspection_works_on_a_read_only_session()
    {
        Require();
        var schema = await _conn!.IntrospectAsync(CancellationToken.None);
        Assert.NotEmpty(schema.Schemas);
    }

    private sealed class Sink : IEngineOutput
    {
        public readonly ConcurrentQueue<JsonElement> Events = new();
        public void SendEvent(string json) => Events.Enqueue(JsonDocument.Parse(json).RootElement.Clone());
        public void SendBatch(int queryId, EncodedBatch batch) { }
        public JsonElement Reply(int id) => Events.Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string id) => _d.GetValueOrDefault(id);
        public void Set(string id, string secret) => _d[id] = secret;
        public void Remove(string id) => _d.Remove(id);
    }

    [SkippableFact]
    public async Task Through_the_engine_a_saved_read_only_connection_blocks_switching_it_off_before_sending()
    {
        Require();
        var dir = Directory.CreateTempSubdirectory("siftdb-ro-");
        try
        {
            var sink = new Sink();
            var pg = new PostgresProvider();
            var store = new ConnectionStore(Path.Combine(dir.FullName, "connections.json"), new MemorySecrets());
            await using var engine = new EngineHost([pg], sink, store);

            // Save the test server as a read-only connection, the way "Save as…" does from a pasted string.
            var parsed = pg.ParseConnectionString(_cs!);
            await engine.HandleCommandAsync(JsonSerializer.Serialize(new
            {
                type = "saveConnection", id = 1, fromConnectionString = _cs,
                connection = new { name = "ro", provider = "postgres", fields = parsed.Fields, readOnly = true, rememberPassword = true },
            }));
            var savedId = sink.Reply(1).GetProperty("savedId").GetString();

            await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 2, savedId }));
            var connected = sink.Reply(2);
            Assert.Equal("connected", connected.GetProperty("type").GetString());
            Assert.True(connected.GetProperty("readOnly").GetBoolean());
            var connectionId = connected.GetProperty("connectionId").GetInt32();

            const string sql = "set default_transaction_read_only = off";
            await engine.HandleCommandAsync(JsonSerializer.Serialize(new
            {
                type = "analyzeSheet", id = 3, connectionId, text = sql, selectionStart = 0, selectionEnd = 0, mode = "assisted",
            }));
            Assert.Contains("read-only", sink.Reply(3).GetProperty("pick").GetProperty("blocked").GetString());

            await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = 4, connectionId, sql }));
            Assert.Equal("error", sink.Reply(4).GetProperty("type").GetString());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
