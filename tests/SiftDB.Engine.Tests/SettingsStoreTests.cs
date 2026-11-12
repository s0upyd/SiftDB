using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>User settings on disk (design section 10): defaults when absent, careful writes, a newer file left alone.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-settings-");
    private string FilePath => Path.Combine(_dir.FullName, "settings.json");

    public void Dispose() => _dir.Delete(recursive: true);

    private SettingsStore NewStore() => new(FilePath);

    private static UserSettings Mode(string mode) => UserSettings.Default with { NewSheetMode = mode };

    [Fact]
    public void Without_a_file_the_defaults_apply_and_nothing_is_written()
    {
        var store = NewStore();
        Assert.Equal("assisted", store.Get().NewSheetMode);
        Assert.Null(store.LoadError);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_saved_mode_survives_a_restart()
    {
        NewStore().Save(Mode("strict"));
        Assert.Equal("strict", NewStore().Get().NewSheetMode);
    }

    [Fact]
    public void An_unknown_mode_reads_and_saves_as_assisted()
    {
        Assert.Equal("assisted", NewStore().Save(Mode("loose")).NewSheetMode);
        File.WriteAllText(FilePath, """{ "version": 1, "newSheetMode": "STRICTISH" }""");
        Assert.Equal("assisted", NewStore().Get().NewSheetMode);
    }

    [Fact]
    public void Settings_a_newer_version_added_are_kept_on_rewrite()
    {
        File.WriteAllText(FilePath, """{ "version": 1, "newSheetMode": "assisted", "resultStyling": { "nullTint": true } }""");
        NewStore().Save(Mode("strict"));
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal("strict", doc.RootElement.GetProperty("newSheetMode").GetString());
        Assert.True(doc.RootElement.GetProperty("resultStyling").GetProperty("nullTint").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "version": 2, "newSheetMode": "strict" }""", "newer SiftDB")]
    [InlineData("{ not json", "could not be read")]
    public void A_file_it_cannot_read_gives_the_defaults_and_is_never_overwritten(string content, string expected)
    {
        File.WriteAllText(FilePath, content);
        var store = NewStore();
        Assert.Equal("assisted", store.Get().NewSheetMode);
        Assert.Contains(expected, store.LoadError);
        Assert.Throws<InvalidDataException>(() => store.Save(Mode("strict")));
        Assert.Equal(content, File.ReadAllText(FilePath));
    }
}

/// <summary>The engine's settings commands, as the UI uses them.</summary>
public sealed class EngineSettingsTests : IAsyncDisposable
{
    private sealed class Sink : IEngineOutput
    {
        public readonly List<string> Raw = new();
        public void SendEvent(string json) { lock (Raw) Raw.Add(json); }
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-settings-");
    private readonly Sink _sink = new();
    private int _nextId;

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _dir.Delete(recursive: true);
    }

    private EngineHost NewEngine(bool withStore = true) =>
        new([], _sink, settings: withStore ? new SettingsStore(Path.Combine(_dir.FullName, "settings.json")) : null);

    private async Task<JsonElement> Send(EngineHost engine, object command)
    {
        var id = ++_nextId;
        var json = JsonSerializer.SerializeToNode(command)!.AsObject();
        json["id"] = id;
        await engine.HandleCommandAsync(json.ToJsonString());
        lock (_sink.Raw)
            return _sink.Raw.Select(e => JsonDocument.Parse(e).RootElement.Clone())
                .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    [Fact]
    public async Task Saving_the_mode_is_answered_with_the_settings_and_a_new_engine_reads_it_back()
    {
        await using (var engine = NewEngine())
        {
            var first = await Send(engine, new { type = "getSettings" });
            Assert.Equal("settings", first.GetProperty("type").GetString());
            Assert.Equal("assisted", first.GetProperty("settings").GetProperty("newSheetMode").GetString());

            var saved = await Send(engine, new { type = "saveSettings", settings = new { newSheetMode = "strict" } });
            Assert.Equal("strict", saved.GetProperty("settings").GetProperty("newSheetMode").GetString());
        }
        await using var again = NewEngine();
        var r = await Send(again, new { type = "getSettings" });
        Assert.Equal("strict", r.GetProperty("settings").GetProperty("newSheetMode").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Without_a_store_the_defaults_apply_and_saving_is_refused()
    {
        await using var engine = NewEngine(withStore: false);
        var r = await Send(engine, new { type = "getSettings" });
        Assert.Equal("assisted", r.GetProperty("settings").GetProperty("newSheetMode").GetString());
        var saved = await Send(engine, new { type = "saveSettings", settings = new { newSheetMode = "strict" } });
        Assert.Equal("error", saved.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_unreadable_file_is_reported_and_saving_says_why()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "settings.json"), """{ "version": 9 }""");
        await using var engine = NewEngine();
        var r = await Send(engine, new { type = "getSettings" });
        Assert.Contains("newer SiftDB", r.GetProperty("error").GetString());
        var saved = await Send(engine, new { type = "saveSettings", settings = new { newSheetMode = "strict" } });
        Assert.Equal("error", saved.GetProperty("type").GetString());
        Assert.Contains("left as it is", saved.GetProperty("message").GetString());
    }
}
