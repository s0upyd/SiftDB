using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>"analyzeSheet" with identifier resolution, through the real EngineHost (no database).</summary>
public class SheetAnalysisWireTests
{
    private static RelationInfo Rel(string name, params string[] cols) =>
        new(name, RelationKind.Table, cols.Select(c => new ColumnInfo(c, "text", true, null)).ToList(), [], [], []);

    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [Rel("Person", "PersonId", "FirstName"), Rel("Email", "EmailId", "PersonId"), Rel("orders", "id")]),
    ]);

    private static async Task<JsonElement> Analyze(string text, int cursor, string? mode, bool schemaLoaded = true)
    {
        var cap = new Capture();
        await using var engine = new EngineHost([new InMemoryProvider(Schema)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"postgres","connectionString":"x"}""");
        if (schemaLoaded) await engine.HandleCommandAsync("""{"type":"introspect","id":2,"connectionId":1}""");
        var cmd = new Dictionary<string, object?>
        {
            ["type"] = "analyzeSheet", ["id"] = 3, ["connectionId"] = 1, ["text"] = text,
            ["selectionStart"] = cursor, ["selectionEnd"] = cursor,
        };
        if (mode is not null) cmd["mode"] = mode;
        await engine.HandleCommandAsync(JsonSerializer.Serialize(cmd));
        return JsonDocument.Parse(cap.Events.Single(e => e.Contains("sheetAnalysis"))).RootElement.Clone();
    }

    [Fact]
    public async Task Assisted_mode_sends_the_corrections_and_a_pick_whose_sql_has_them_applied()
    {
        var r = await Analyze("select * from Person", 5, "assisted");

        var c = r.GetProperty("corrections").EnumerateArray().Single();
        Assert.Equal("select * from ".Length, c.GetProperty("start").GetInt32());
        Assert.Equal("Person".Length, c.GetProperty("length").GetInt32());
        Assert.Equal("\"Person\"", c.GetProperty("text").GetString());

        var pick = r.GetProperty("pick");
        Assert.Equal("cursorStatement", pick.GetProperty("reason").GetString());
        Assert.Equal("select * from \"Person\"", pick.GetProperty("sql").GetString());
        Assert.Equal(JsonValueKind.Null, pick.GetProperty("blocked").ValueKind);
    }

    [Fact]
    public async Task Strict_mode_runs_the_text_as_written_but_blocks_with_a_reason()
    {
        var pick = (await Analyze("select * from Person", 5, "strict")).GetProperty("pick");
        Assert.Equal("select * from Person", pick.GetProperty("sql").GetString());
        Assert.Contains("identifier problem", pick.GetProperty("blocked").GetString());
    }

    [Fact]
    public async Task The_mode_defaults_to_assisted()
    {
        var pick = (await Analyze("select * from Person", 5, mode: null)).GetProperty("pick");
        Assert.Equal("select * from \"Person\"", pick.GetProperty("sql").GetString());
    }

    [Fact]
    public async Task Issues_carry_their_code_severity_and_fix_in_camelCase()
    {
        var r = await Analyze("select * from person", 5, "assisted");
        var issue = r.GetProperty("issues").EnumerateArray().Single();
        Assert.Equal("caseMismatch", issue.GetProperty("code").GetString());
        Assert.Equal("warning", issue.GetProperty("severity").GetString());
        Assert.Equal("\"Person\"", issue.GetProperty("fixText").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("pick").GetProperty("blocked").ValueKind); // a warning does not block here
    }

    [Fact]
    public async Task An_unknown_column_is_sent_with_its_candidates_and_never_blocks()
    {
        const string sql = "select * from \"Person\" where id = 1";
        foreach (var mode in new[] { "assisted", "strict" })
        {
            var r = await Analyze(sql, 5, mode);
            var issue = r.GetProperty("issues").EnumerateArray().Single();
            Assert.Equal("unknownColumn", issue.GetProperty("code").GetString());
            Assert.Equal("warning", issue.GetProperty("severity").GetString());
            Assert.Equal(["\"PersonId\""], issue.GetProperty("fixes").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("\"PersonId\"", issue.GetProperty("fixText").GetString());
            var pick = r.GetProperty("pick");
            Assert.Equal(sql, pick.GetProperty("sql").GetString());                 // runs as written: the server decides
            Assert.Equal(JsonValueKind.Null, pick.GetProperty("blocked").ValueKind);
        }
    }

    [Fact]
    public async Task An_ambiguous_column_blocks_even_in_assisted_mode()
    {
        var pick = (await Analyze("select PersonId from \"Person\" p, \"Email\" e", 5, "assisted")).GetProperty("pick");
        Assert.Contains("could mean", pick.GetProperty("blocked").GetString());
    }

    [Fact]
    public async Task Only_the_statement_that_runs_is_prepared()
    {
        const string sheet = "select * from Person;\nselect * from Email;";
        var cursorInSecond = sheet.IndexOf("Email", StringComparison.Ordinal);
        var r = await Analyze(sheet, cursorInSecond, "assisted");

        Assert.Equal("select * from \"Email\"", r.GetProperty("pick").GetProperty("sql").GetString());
        Assert.Equal(2, r.GetProperty("corrections").GetArrayLength()); // the margin still shows both statements' fixes
    }

    [Fact]
    public async Task Without_a_loaded_schema_nothing_is_corrected_and_the_text_runs_as_written()
    {
        var r = await Analyze("select * from Person", 5, "assisted", schemaLoaded: false);
        Assert.Equal(0, r.GetProperty("corrections").GetArrayLength());
        Assert.Equal(0, r.GetProperty("issues").GetArrayLength());
        Assert.Equal("select * from Person", r.GetProperty("pick").GetProperty("sql").GetString());
    }

    [Fact]
    public async Task A_selection_runs_trimmed_with_the_corrections_inside_it()
    {
        const string sheet = "select 1;\n  select * from Person  ;";
        var start = sheet.IndexOf("select * from", StringComparison.Ordinal) - 2;
        var cap = new Capture();
        await using var engine = new EngineHost([new InMemoryProvider(Schema)], cap);
        await engine.HandleCommandAsync("""{"type":"connect","id":1,"provider":"postgres","connectionString":"x"}""");
        await engine.HandleCommandAsync("""{"type":"introspect","id":2,"connectionId":1}""");
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new
        {
            type = "analyzeSheet", id = 3, connectionId = 1, text = sheet,
            selectionStart = start, selectionEnd = sheet.Length - 1, mode = "assisted",
        }));
        var pick = JsonDocument.Parse(cap.Events.Single(e => e.Contains("sheetAnalysis"))).RootElement.GetProperty("pick");
        Assert.Equal("selection", pick.GetProperty("reason").GetString());
        Assert.Equal("select * from \"Person\"", pick.GetProperty("sql").GetString());
    }
}
