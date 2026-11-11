using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>The "complete" command through the real EngineHost, with Postgres parsing and quoting but no database.</summary>
public class CompletionWireTests
{
    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [
            new RelationInfo("Person", RelationKind.Table,
                [new ColumnInfo("PersonId", "integer", false, null)], ["PersonId"], [], []),
            new RelationInfo("orders", RelationKind.View, [], [], [], []),
        ]),
    ]);

    private static async Task<(EngineHost Engine, Capture Cap)> Connected(bool introspect)
    {
        var cap = new Capture();
        var engine = new EngineHost([new InMemoryProvider(Schema)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"postgres","connectionString":"x"}""");
        if (introspect) await engine.HandleCommandAsync("""{"type":"introspect","id":2,"connectionId":1}""");
        return (engine, cap);
    }

    private static string Command(string text, int offset) =>
        JsonSerializer.Serialize(new { type = "complete", id = 9, connectionId = 1, text, offset });

    [Fact]
    public async Task Complete_replies_with_items_range_and_camelCase_kinds()
    {
        var (engine, cap) = await Connected(introspect: true);
        await using var _ = engine;
        const string text = "select * from per";
        await engine.HandleCommandAsync(Command(text, text.Length));

        using var doc = JsonDocument.Parse(cap.Events.Single(e => e.Contains("\"completions\"")));
        var root = doc.RootElement;
        Assert.Equal(9, root.GetProperty("replyTo").GetInt32());
        Assert.Equal(text.Length - 3, root.GetProperty("start").GetInt32());
        Assert.Equal(text.Length, root.GetProperty("end").GetInt32());

        var item = root.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Person", item.GetProperty("label").GetString());
        Assert.Equal("\"Person\"", item.GetProperty("insertText").GetString());
        Assert.Equal("table", item.GetProperty("kind").GetString());
        Assert.Equal("Person", item.GetProperty("filterText").GetString());
    }

    [Fact]
    public async Task Views_and_columns_use_their_own_kind_names()
    {
        var (engine, cap) = await Connected(introspect: true);
        await using var _ = engine;

        await engine.HandleCommandAsync(Command("select * from ord", 17));
        await engine.HandleCommandAsync(Command("select p. from \"Person\" p", 9));

        var replies = cap.Events.Where(e => e.Contains("\"completions\"")).Select(e => JsonDocument.Parse(e)).ToList();
        Assert.Equal("view", replies[0].RootElement.GetProperty("items")[0].GetProperty("kind").GetString());
        Assert.Equal("column", replies[1].RootElement.GetProperty("items")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_quiet_introspect_still_fills_the_cache_that_completion_reads()
    {
        // A newly opened sheet refreshes its own session's schema without sending the schema to the UI again.
        var cap = new Capture();
        await using var engine = new EngineHost([new InMemoryProvider(Schema)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"postgres","connectionString":"x"}""");
        await engine.HandleCommandAsync("""{"type":"introspect","id":2,"connectionId":1,"send":false}""");
        Assert.DoesNotContain(cap.Events, e => e.Contains("\"schema\""));

        await engine.HandleCommandAsync(Command("select * from per", 17));

        using var doc = JsonDocument.Parse(cap.Events.Single(e => e.Contains("\"completions\"")));
        Assert.Equal("Person", doc.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("label").GetString());
    }

    [Fact]
    public async Task Without_a_loaded_schema_it_answers_with_no_items_instead_of_failing()
    {
        var (engine, cap) = await Connected(introspect: false);
        await using var _ = engine;
        await engine.HandleCommandAsync(Command("select * from ", 14));

        using var doc = JsonDocument.Parse(cap.Events.Single(e => e.Contains("\"completions\"")));
        Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_connection_replies_with_an_error()
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new InMemoryProvider(Schema)], cap);
        await engine.HandleCommandAsync("""{"type":"complete","id":4,"connectionId":99,"text":"x","offset":0}""");
        Assert.Contains(cap.Events, e => e.Contains("\"error\"") && e.Contains("\"replyTo\":4"));
    }
}
