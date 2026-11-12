using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>The engine's saved-connection commands (design section 8) against a fake provider.</summary>
public sealed class EngineConnectionTests : IAsyncLifetime
{
    /// <summary>A server that wants the password "right", and a dialect where "UNLOCK" switches read-only off.</summary>
    private sealed class Provider : IDatabaseProvider
    {
        public readonly ConcurrentQueue<(string ConnectionString, ConnectionOptions Options)> Opened = new();
        public string Id => "fake";
        public string DisplayName => "Fake";
        public int ContractVersion => ProviderContract.CurrentVersion;
        public ISqlParser Parser { get; } = new WholeTextParser();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();
        public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
            [new("host", "Host", ConnectionFieldKind.Text, "server", Required: true), new("port", "Port", ConnectionFieldKind.Number, "server")];
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Server;

        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password)
        {
            if (fields.TryGetValue("port", out var port) && !int.TryParse(port, out _)) throw new ArgumentException($"Port '{port}' is not a port number.");
            return $"host={fields.GetValueOrDefault("host")};password={password}";
        }

        public ParsedConnection ParseConnectionString(string cs)
        {
            var parts = cs.Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            return new ParsedConnection(new Dictionary<string, string> { ["host"] = parts["host"] }, parts.GetValueOrDefault("password"));
        }

        public string? CheckReadOnly(string sql) => sql.Contains("UNLOCK") ? "would switch read-only off" : null;

        public bool CannotRunInTransaction(string sql) => false;

        public Task<IProviderConnection> OpenAsync(string cs, ConnectionOptions options, CancellationToken ct)
        {
            Opened.Enqueue((cs, options));
            if (!cs.EndsWith("password=right", StringComparison.Ordinal))
                throw new ConnectionAuthenticationException("password authentication failed", new Exception());
            return Task.FromResult<IProviderConnection>(new Conn());
        }

        private sealed class WholeTextParser : ISqlParser
        {
            public IReadOnlyList<SqlToken> Tokenize(string sql) => [];
            public IReadOnlyList<SqlRange> Split(string sql) => sql.Trim().Length == 0 ? [] : [new SqlRange(0, sql.Length)];
            public SqlSyntaxError? Validate(string statement) => null;
        }

        private sealed class Conn : IProviderConnection
        {
            public Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
            {
                var t = new DataTable();
                t.Columns.Add("x", typeof(int));
                return Task.FromResult<DbDataReader>(t.CreateDataReader());
            }
            public ITransactionMonitor Transactions { get; } = new NullMonitor();
            public Task<DatabaseSchema> IntrospectAsync(CancellationToken ct) => Task.FromResult(new DatabaseSchema([]));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class NullMonitor : ITransactionMonitor
        {
            public bool CanDetectWrites => true;
            public Task BeginAsync(CancellationToken ct) => Task.CompletedTask;
            public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(new TransactionSnapshot(TransactionState.None, null));
        }
    }

    private sealed class Sink : IEngineOutput
    {
        public readonly ConcurrentQueue<string> Raw = new();
        public void SendEvent(string json) => Raw.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-engine-conn-");
    private readonly MemorySecrets _secrets = new();
    private readonly Provider _provider = new();
    private readonly Sink _sink = new();
    private EngineHost _engine = null!;
    private int _nextId;

    public Task InitializeAsync()
    {
        _engine = new EngineHost([_provider], _sink, new ConnectionStore(Path.Combine(_dir.FullName, "connections.json"), _secrets));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    /// <summary>Sends a command and returns the engine's reply to it.</summary>
    private async Task<JsonElement> Send(object command)
    {
        var id = ++_nextId;
        var json = JsonSerializer.SerializeToNode(command)!.AsObject();
        json["id"] = id;
        await _engine.HandleCommandAsync(json.ToJsonString());
        return _sink.Raw.Select(e => JsonDocument.Parse(e).RootElement.Clone())
            .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    private async Task<string> Save(string name, bool remember, string? password = null, bool readOnly = false, string? id = null)
    {
        object connection = new { id, name, provider = "fake", fields = new { host = "h" }, rememberPassword = remember, readOnly };
        var reply = password is null
            ? await Send(new { type = "saveConnection", connection })
            : await Send(new { type = "saveConnection", connection, password });
        Assert.Equal("connectionSaved", Type(reply));
        return reply.GetProperty("savedId").GetString()!;
    }

    [Fact]
    public async Task Lists_providers_fields_and_connections_without_ever_sending_a_password()
    {
        var id = await Save("dev", remember: true, password: "right");
        var list = await Send(new { type = "listConnections" });
        Assert.Equal("connections", Type(list));
        var provider = list.GetProperty("providers")[0];
        Assert.Equal("fake", provider.GetProperty("id").GetString());
        Assert.Equal("host", provider.GetProperty("fields")[0].GetProperty("key").GetString());
        Assert.Equal("server", provider.GetProperty("readOnlyEnforcement").GetString());
        var c = list.GetProperty("connections")[0];
        Assert.Equal(id, c.GetProperty("id").GetString());
        Assert.True(c.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("right", string.Join("\n", _sink.Raw));
    }

    [Fact]
    public async Task A_remembered_password_connects_straight_away_and_becomes_last_used()
    {
        var id = await Save("dev", remember: true, password: "right", readOnly: true);
        var reply = await Send(new { type = "connect", savedId = id });
        Assert.Equal("connected", Type(reply));
        Assert.Equal(id, reply.GetProperty("savedId").GetString());
        Assert.True(reply.GetProperty("readOnly").GetBoolean());
        Assert.True(_provider.Opened.Last().Options.ReadOnly);
        Assert.Equal(id, (await Send(new { type = "listConnections" })).GetProperty("lastUsed").GetString());
    }

    [Fact]
    public async Task Without_a_remembered_password_it_asks_once_per_run()
    {
        var id = await Save("dev", remember: false);
        var first = await Send(new { type = "connect", savedId = id });
        Assert.Equal("passwordNeeded", Type(first));
        Assert.False(first.GetProperty("retry").GetBoolean());
        Assert.Empty(_provider.Opened); // nothing was tried without one

        Assert.Equal("connected", Type(await Send(new { type = "connect", savedId = id, password = "right" })));
        Assert.Empty(_secrets.Values); // not remembered on disk…
        Assert.Equal("connected", Type(await Send(new { type = "connect", savedId = id }))); // …but a second sheet does not ask
    }

    [Fact]
    public async Task A_wrong_password_asks_again_and_is_not_offered_to_the_next_sheet()
    {
        var id = await Save("dev", remember: false);
        var wrong = await Send(new { type = "connect", savedId = id, password = "wrong" });
        Assert.Equal("passwordNeeded", Type(wrong));
        Assert.True(wrong.GetProperty("retry").GetBoolean());
        Assert.Equal("passwordNeeded", Type(await Send(new { type = "connect", savedId = id })));
    }

    [Fact]
    public async Task A_remembered_password_that_has_gone_stale_asks_too()
    {
        var id = await Save("dev", remember: true, password: "old");
        var reply = await Send(new { type = "connect", savedId = id });
        Assert.Equal("passwordNeeded", Type(reply));
        Assert.True(reply.GetProperty("retry").GetBoolean());
    }

    [Fact]
    public async Task Saving_keeps_the_password_unless_a_new_one_is_given()
    {
        var id = await Save("dev", remember: true, password: "right");
        await Save("dev renamed", remember: true, id: id);
        Assert.Equal("right", _secrets.Get(id));
        Assert.Equal("connected", Type(await Send(new { type = "connect", savedId = id })));
    }

    [Fact]
    public async Task Save_as_takes_the_password_from_the_pasted_string()
    {
        var parsed = await Send(new { type = "parseConnectionString", provider = "fake", connectionString = "host=h;password=right" });
        Assert.Equal("parsedConnection", Type(parsed));
        Assert.True(parsed.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("right", parsed.GetRawText());

        var saved = await Send(new
        {
            type = "saveConnection", fromConnectionString = "host=h;password=right",
            connection = new { name = "pasted", provider = "fake", fields = parsed.GetProperty("fields"), rememberPassword = true },
        });
        Assert.Equal("right", _secrets.Get(saved.GetProperty("savedId").GetString()!));
    }

    [Fact]
    public async Task Bad_values_and_duplicate_names_are_refused_when_saving()
    {
        await Save("dev", remember: false);
        var dup = await Send(new { type = "saveConnection", connection = new { name = "Dev", provider = "fake", fields = new { host = "h" } } });
        Assert.Equal("error", Type(dup));
        var badPort = await Send(new { type = "saveConnection", connection = new { name = "x", provider = "fake", fields = new { host = "h", port = "abc" } } });
        Assert.Equal("error", Type(badPort));
        Assert.Contains("Port", badPort.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_connection_in_use_cannot_be_deleted()
    {
        var id = await Save("dev", remember: true, password: "right");
        var session = (await Send(new { type = "connect", savedId = id })).GetProperty("connectionId").GetInt32();
        var refused = await Send(new { type = "deleteConnection", savedId = id });
        Assert.Equal("error", Type(refused));
        Assert.Contains("A sheet is connected", refused.GetProperty("message").GetString());

        await Send(new { type = "disconnect", connectionId = session });
        Assert.Equal("ok", Type(await Send(new { type = "deleteConnection", savedId = id })));
        Assert.Empty((await Send(new { type = "listConnections" })).GetProperty("connections").EnumerateArray());
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task Test_uses_what_the_form_holds_and_reports_a_turned_down_password()
    {
        var ok = await Send(new { type = "testConnection", connection = new { provider = "fake", fields = new { host = "h" }, readOnly = true }, password = "right" });
        Assert.Equal("ok", Type(ok));
        Assert.True(_provider.Opened.Last().Options.ReadOnly);

        var bad = await Send(new { type = "testConnection", connection = new { provider = "fake", fields = new { host = "h" } }, password = "nope" });
        Assert.Equal("error", Type(bad));
        Assert.Contains("turned the password down", bad.GetProperty("message").GetString());

        var id = await Save("dev", remember: true, password: "right"); // editing a saved one: its saved password is used
        Assert.Equal("ok", Type(await Send(new { type = "testConnection", connection = new { id, provider = "fake", fields = new { host = "h" } } })));
    }

    [Fact]
    public async Task A_read_only_session_blocks_switching_it_off_in_analysis_and_execution()
    {
        var id = await Save("ro", remember: true, password: "right", readOnly: true);
        var connectionId = (await Send(new { type = "connect", savedId = id })).GetProperty("connectionId").GetInt32();

        var analysis = await Send(new { type = "analyzeSheet", connectionId, text = "UNLOCK", selectionStart = 0, selectionEnd = 0, mode = "assisted" });
        Assert.Equal("would switch read-only off", analysis.GetProperty("pick").GetProperty("blocked").GetString());
        var run = await Send(new { type = "execute", connectionId, sql = "UNLOCK" });
        Assert.Equal("error", Type(run));

        var fine = await Send(new { type = "analyzeSheet", connectionId, text = "select", selectionStart = 0, selectionEnd = 0, mode = "assisted" });
        Assert.Equal(JsonValueKind.Null, fine.GetProperty("pick").GetProperty("blocked").ValueKind);
    }

    [Fact]
    public async Task A_writable_session_is_not_checked()
    {
        var id = await Save("rw", remember: true, password: "right");
        var connectionId = (await Send(new { type = "connect", savedId = id })).GetProperty("connectionId").GetInt32();
        var analysis = await Send(new { type = "analyzeSheet", connectionId, text = "UNLOCK", selectionStart = 0, selectionEnd = 0, mode = "assisted" });
        Assert.Equal(JsonValueKind.Null, analysis.GetProperty("pick").GetProperty("blocked").ValueKind);
    }

    [Fact]
    public async Task A_pasted_connection_string_still_connects_writable()
    {
        var reply = await Send(new { type = "connect", provider = "fake", connectionString = "host=h;password=right" });
        Assert.Equal("connected", Type(reply));
        Assert.False(reply.GetProperty("readOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("savedId").ValueKind);
    }

    [Fact]
    public async Task Without_a_store_saved_connections_say_so()
    {
        await using var bare = new EngineHost([_provider], _sink);
        await bare.HandleCommandAsync("""{"type":"listConnections","id":99}""");
        Assert.Contains("not available", _sink.Raw.Last());
    }
}
