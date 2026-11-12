using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>The engine's file and session commands (design section 6).</summary>
public sealed class EngineSheetTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        public readonly List<string> Raw = new();
        public void SendEvent(string json) { lock (Raw) Raw.Add(json); }
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-sheets-");
    private readonly Sink _sink = new();
    private ConnectionStore _store = null!;
    private EngineHost _engine = null!;
    private int _nextId;

    public Task InitializeAsync()
    {
        _store = new ConnectionStore(Path.Combine(_dir.FullName, "connections.json"), new MemorySecrets());
        _engine = new EngineHost([], _sink, _store, new SessionStore(Path.Combine(_dir.FullName, "session")));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    private async Task<JsonElement> Send(object command)
    {
        var id = ++_nextId;
        var json = JsonSerializer.SerializeToNode(command)!.AsObject();
        json["id"] = id;
        await _engine.HandleCommandAsync(json.ToJsonString());
        lock (_sink.Raw)
            return _sink.Raw.Select(e => JsonDocument.Parse(e).RootElement.Clone())
                .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private SavedConnection Saved(string name) =>
        _store.Save(new SavedConnection("", name, "postgres", new Dictionary<string, string> { ["host"] = "h" }, false, false), null);

    private string FilePath(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public async Task Opening_a_file_matches_its_connection_by_name_ignoring_case()
    {
        var dev = Saved("Dev-PG");
        File.WriteAllText(FilePath("a.sql"), "-- db: connection=dev-pg; mode=strict\r\nselect 1;\r\n");
        var r = await Send(new { type = "openFile", path = FilePath("a.sql") });
        Assert.Equal("fileOpened", r.GetProperty("type").GetString());
        Assert.Equal("a.sql", r.GetProperty("fileName").GetString());
        Assert.Equal("select 1;\n", r.GetProperty("text").GetString());
        Assert.Equal(dev.Id, r.GetProperty("savedId").GetString());
        Assert.Equal("strict", r.GetProperty("mode").GetString());
        Assert.Equal("crlf", r.GetProperty("lineEnding").GetString());
    }

    [Fact]
    public async Task A_connection_not_saved_here_comes_back_by_name_only()
    {
        File.WriteAllText(FilePath("b.sql"), "-- db: connection=prod-ro\nselect 1;");
        var r = await Send(new { type = "openFile", path = FilePath("b.sql") });
        Assert.Equal("prod-ro", r.GetProperty("connectionName").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("savedId").ValueKind);
    }

    [Fact]
    public async Task Saving_names_the_saved_connection_by_its_current_name()
    {
        var dev = Saved("dev");
        var r = await Send(new { type = "saveFile", path = FilePath("c.sql"), text = "select 1;\n", savedId = dev.Id, mode = "assisted", lineEnding = "lf", encoding = "utf-8" });
        Assert.Equal("fileSaved", r.GetProperty("type").GetString());
        Assert.Equal("-- db: connection=dev; mode=assisted\nselect 1;\n", File.ReadAllText(FilePath("c.sql")));
    }

    [Fact]
    public async Task Saving_keeps_an_unknown_connection_name_and_other_header_keys()
    {
        await Send(new
        {
            type = "saveFile", path = FilePath("d.sql"), text = "x", connectionName = "prod-ro", mode = "strict", lineEnding = "lf",
            headerOther = new[] { new[] { "vars", "Smith" } },
        });
        Assert.Equal("-- db: connection=prod-ro; mode=strict; vars=Smith\nx", File.ReadAllText(FilePath("d.sql")));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_an_error_not_a_crash()
    {
        File.WriteAllBytes(FilePath("bad.sql"), [0x73, 0xE9, 0x3B]);
        var r = await Send(new { type = "openFile", path = FilePath("bad.sql") });
        Assert.Equal("error", r.GetProperty("type").GetString());
        Assert.Contains("not UTF-8", r.GetProperty("message").GetString());
        var missing = await Send(new { type = "openFile", path = FilePath("nope.sql") });
        Assert.Equal("error", missing.GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_session_round_trips_and_a_deleted_connection_comes_back_by_name()
    {
        var dev = Saved("dev");
        await Send(new
        {
            type = "saveSession", active = "s2",
            sheets = new object[]
            {
                new { id = "s1", title = "Sheet 1", dirty = true, savedId = dev.Id, connectionName = "dev", mode = "strict", text = "select 1" },
                new { id = "s2", title = "Sheet 2", dirty = true, mode = "assisted", text = "select 2" },
            },
        });
        _store.Delete(dev.Id);

        var r = await Send(new { type = "loadSession" });
        Assert.Equal("s2", r.GetProperty("active").GetString());
        var first = r.GetProperty("sheets")[0];
        Assert.Equal("select 1", first.GetProperty("text").GetString());
        Assert.Equal("strict", first.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("savedId").ValueKind);
        Assert.Equal("dev", first.GetProperty("connectionName").GetString());
    }
}
