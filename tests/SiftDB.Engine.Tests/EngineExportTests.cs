using System.Text;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>Exporting a result (design section 11, Export): pieces of text in, one whole file out, or nothing at all.</summary>
public sealed class EngineExportTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        public readonly List<string> Raw = new();
        public void SendEvent(string json) { lock (Raw) Raw.Add(json); }
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-export-");
    private readonly Sink _sink = new();
    private EngineHost _engine = null!;
    private int _nextId;

    public Task InitializeAsync()
    {
        _engine = new EngineHost([], _sink);
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

    private string FilePath(string name) => Path.Combine(_dir.FullName, name);

    private async Task<int> Begin(string path, bool bom)
    {
        var r = await Send(new { type = "exportBegin", path, byteOrderMark = bom });
        Assert.Equal("exportStarted", r.GetProperty("type").GetString());
        return r.GetProperty("exportId").GetInt32();
    }

    private string[] TempFiles() => Directory.GetFiles(_dir.FullName, "*.tmp");

    [Fact]
    public async Task Pieces_are_written_in_order_as_UTF8_with_a_byte_order_mark_when_asked()
    {
        var path = FilePath("out.csv");
        var export = await Begin(path, bom: true);
        Assert.Equal("ok", (await Send(new { type = "exportWrite", exportId = export, text = "id,name\r\n" })).GetProperty("type").GetString());
        await Send(new { type = "exportWrite", exportId = export, text = "1,Zoë\r\n" });
        var done = await Send(new { type = "exportEnd", exportId = export });

        Assert.Equal("exportFinished", done.GetProperty("type").GetString());
        Assert.Equal("out.csv", done.GetProperty("fileName").GetString());
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("id,name\r\n1,Zoë\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Without_a_byte_order_mark_the_file_is_plain_UTF8()
    {
        var path = FilePath("out.json");
        var export = await Begin(path, bom: false);
        await Send(new { type = "exportWrite", exportId = export, text = "[]" });
        await Send(new { type = "exportEnd", exportId = export });
        Assert.Equal("[]"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task The_file_it_replaces_is_untouched_until_the_export_finishes()
    {
        var path = FilePath("out.csv");
        File.WriteAllText(path, "old");
        var export = await Begin(path, bom: false);
        await Send(new { type = "exportWrite", exportId = export, text = "new" });
        Assert.Equal("old", File.ReadAllText(path));
        await Send(new { type = "exportEnd", exportId = export });
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_cancelled_export_leaves_no_file_and_keeps_the_old_one()
    {
        var kept = FilePath("kept.csv");
        File.WriteAllText(kept, "old");
        var export = await Begin(kept, bom: false);
        await Send(new { type = "exportWrite", exportId = export, text = "new" });
        var r = await Send(new { type = "exportEnd", exportId = export, cancel = true });

        Assert.Equal("ok", r.GetProperty("type").GetString());
        Assert.Equal("old", File.ReadAllText(kept));
        Assert.Empty(TempFiles());

        var fresh = FilePath("fresh.csv");
        var second = await Begin(fresh, bom: false);
        await Send(new { type = "exportEnd", exportId = second, cancel = true });
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public async Task An_ended_export_takes_no_more_text()
    {
        var export = await Begin(FilePath("out.csv"), bom: false);
        await Send(new { type = "exportEnd", exportId = export });
        Assert.Equal("error", (await Send(new { type = "exportWrite", exportId = export, text = "x" })).GetProperty("type").GetString());
        Assert.Equal("error", (await Send(new { type = "exportEnd", exportId = export })).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_target_open_in_another_program_fails_and_leaves_no_temp_file()
    {
        var path = FilePath("locked.csv");
        File.WriteAllText(path, "old");
        var export = await Begin(path, bom: false);
        await Send(new { type = "exportWrite", exportId = export, text = "new" });
        JsonElement r;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // as Excel holds a CSV it has open
            r = await Send(new { type = "exportEnd", exportId = export });

        if (OperatingSystem.IsWindows()) // only Windows refuses to replace a file another program has open
        {
            Assert.Equal("error", r.GetProperty("type").GetString());
            Assert.Equal("old", File.ReadAllText(path));
        }
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Ending_the_engine_throws_unfinished_exports_away()
    {
        var path = FilePath("out.csv");
        var export = await Begin(path, bom: false);
        await Send(new { type = "exportWrite", exportId = export, text = "partial" });
        await _engine.DisposeAsync();
        Assert.False(File.Exists(path));
        Assert.Empty(TempFiles());
    }
}
