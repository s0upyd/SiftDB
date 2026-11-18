using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>The "formatSql" command (design section 14, F7): which statements it formats, and the one edit it answers with. Needs SIFTDB_SPIKE_CONN.</summary>
public class FormatCommandTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly ConcurrentQueue<string> _events = new();
        public void SendEvent(string json) => _events.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch b) { }
        public JsonElement Reply(int id) => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
            .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request, _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _engine = new EngineHost([new PostgresProvider()], _sink);
        _conn = (await Send(new { type = "connect", provider = "postgres", connectionString = _cs })).GetProperty("connectionId").GetInt32();
    }

    public async Task DisposeAsync() { if (_engine is not null) await _engine.DisposeAsync(); }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task<JsonElement> Send(object cmd)
    {
        var id = ++_request;
        var json = JsonSerializer.SerializeToNode(cmd)!.AsObject();
        json["id"] = id;
        await _engine!.HandleCommandAsync(json.ToJsonString());
        return _sink.Reply(id);
    }

    private Task<JsonElement> Format(string text, int start, int end, bool whole = false) =>
        Send(new { type = "formatSql", connectionId = _conn, text, selectionStart = start, selectionEnd = end, whole });

    /// <summary>The sheet after applying the answer's edit.</summary>
    private static string Apply(string text, JsonElement r) =>
        text.Remove(r.GetProperty("start").GetInt32(), r.GetProperty("length").GetInt32()).Insert(r.GetProperty("start").GetInt32(), r.GetProperty("text").GetString()!);

    [SkippableFact]
    public async Task The_cursor_formats_its_statement_only()
    {
        Require();
        var text = "select a from t where x = 1;\n\nselect b from u where y = 2;";
        var r = await Format(text, 3, 3);
        Assert.Equal("select a\nfrom t\nwhere x = 1;\n\nselect b from u where y = 2;", Apply(text, r));
    }

    [SkippableFact]
    public async Task A_selection_formats_every_statement_it_touches_and_keeps_what_is_between()
    {
        Require();
        var text = "select a from t;\n-- keep me\n\nselect b from u;";
        var r = await Format(text, 10, text.Length - 3);
        Assert.Equal("select a\nfrom t;\n-- keep me\n\nselect b\nfrom u;", Apply(text, r));
    }

    [SkippableFact]
    public async Task The_whole_sheet_formats_what_it_can_and_says_what_it_left()
    {
        Require();
        var text = "select a from t;\nselect from where;\nselect b from u;";
        var r = await Format(text, 0, 0, whole: true);
        Assert.Equal("select a\nfrom t;\nselect from where;\nselect b\nfrom u;", Apply(text, r));
        Assert.Contains("Formatted 2 of 3 statements; line 2 does not parse", r.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task A_statement_that_does_not_parse_is_refused()
    {
        Require();
        var r = await Format("select a from t where", 3, 3);
        Assert.False(r.TryGetProperty("start", out _));
        Assert.StartsWith("Nothing was formatted: line 1 does not parse", r.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task Formatted_text_says_so()
    {
        Require();
        var r = await Format("select a\nfrom t", 0, 0);
        Assert.Equal("Already formatted.", r.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task An_indented_statement_keeps_its_indent()
    {
        Require();
        var text = "    select a from t where x";
        Assert.Equal("    select a\n    from t\n    where x", Apply(text, await Format(text, 6, 6)));
    }
}
