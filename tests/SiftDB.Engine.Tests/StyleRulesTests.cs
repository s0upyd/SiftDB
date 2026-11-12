using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>Result styling rules (design section 10): one list, presets at the bottom, presets kept and never edited.</summary>
public sealed class StyleRulesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-rules-");
    private string FilePath => Path.Combine(_dir.FullName, "settings.json");

    public void Dispose() => _dir.Delete(recursive: true);

    private static StyleRule Rule(string name, string condition = "equals", string[]? values = null, string text = "#000", string background = "#fff") =>
        new("", name, true, "status", [], condition, values ?? ["shipped"], text, background, false);

    private static UserSettings WithRules(params StyleRule[] rules) => UserSettings.Default with { ResultRules = rules };

    [Fact]
    public void By_default_the_list_is_the_presets()
    {
        var ids = new SettingsStore(FilePath).Get().ResultRules.Select(r => r.Id);
        Assert.Equal(["preset:null", "preset:true", "preset:false", "preset:negative"], ids);
    }

    [Fact]
    public void The_boolean_presets_apply_to_Boolean_columns_only()
    {
        var t = StyleRules.Presets.Single(p => p.Id == "preset:true");
        Assert.Equal([GenericType.Boolean], t.Types);
        Assert.Contains("1", t.Values);
    }

    [Fact]
    public void A_user_rule_survives_a_restart_above_the_presets_and_gets_an_id()
    {
        new SettingsStore(FilePath).Save(WithRules(Rule("Shipped")));
        var rules = new SettingsStore(FilePath).Get().ResultRules;
        Assert.Equal("Shipped", rules[0].Name);
        Assert.NotEmpty(rules[0].Id);
        Assert.False(rules[0].IsPreset);
        Assert.Equal(StyleRules.Presets.Select(p => p.Id), rules.Skip(1).Select(r => r.Id));
    }

    [Fact]
    public void A_preset_keeps_its_place_and_on_off_but_never_its_edits()
    {
        var hacked = StyleRules.Presets[3] with { Enabled = false, Text = "#123456", Values = ["oops"] };
        new SettingsStore(FilePath).Save(WithRules(hacked, Rule("Shipped")));
        var rules = new SettingsStore(FilePath).Get().ResultRules;
        Assert.Equal("preset:negative", rules[0].Id);
        Assert.False(rules[0].Enabled);
        Assert.Equal("#cf222e", rules[0].Text); // the definition comes from the app, not the file
        Assert.Equal("Shipped", rules[1].Name);
        Assert.Equal(["preset:null", "preset:true", "preset:false"], rules.Skip(2).Select(r => r.Id)); // missing ones at the bottom
        Assert.DoesNotContain("123456", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("equals", new string[0], "#000", "#fff", "a value")]
    [InlineData("oneOf", new[] { " ", "" }, "#000", "#fff", "at least one value")]
    [InlineData("range", new[] { "", "" }, "#000", "#fff", "lowest or highest")]
    [InlineData("range", new[] { "x", "" }, "#000", "#fff", "lowest or highest")]
    [InlineData("equals", new[] { "a" }, "red", "#fff", "both a text and a background")]
    [InlineData("equals", new[] { "a" }, "#000", "", "both a text and a background")]
    [InlineData("bigger", new[] { "a" }, "#000", "#fff", "unknown condition")]
    public void Saving_a_rule_that_is_not_valid_is_refused_with_the_reason(string condition, string[] values, string text, string bg, string expected)
    {
        var store = new SettingsStore(FilePath);
        var ex = Assert.Throws<ArgumentException>(() => store.Save(WithRules(Rule("Bad", condition, values, text, bg))));
        Assert.Contains(expected, ex.Message);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_rule_in_the_file_that_is_not_valid_is_dropped_on_reading()
    {
        File.WriteAllText(FilePath, """
            { "version": 1, "resultRules": [
              { "id": "a", "name": "ok", "condition": "negative", "text": "#000", "background": "#fff" },
              { "id": "b", "name": "bad", "condition": "negative", "text": "teal", "background": "#fff" },
              { "id": "preset:gone" } ] }
            """);
        var rules = new SettingsStore(FilePath).Get().ResultRules;
        Assert.Equal("ok", rules[0].Name);
        Assert.Equal(1 + StyleRules.Presets.Count, rules.Count);
    }

    [Fact]
    public void Types_are_stored_by_their_wire_names()
    {
        new SettingsStore(FilePath).Save(WithRules(Rule("Dates") with { Types = [GenericType.DateTime, GenericType.Date] }));
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        var types = doc.RootElement.GetProperty("resultRules")[0].GetProperty("types").EnumerateArray().Select(t => t.GetString());
        Assert.Equal(["dateTime", "date"], types);
        Assert.Equal([GenericType.DateTime, GenericType.Date], new SettingsStore(FilePath).Get().ResultRules[0].Types);
    }
}
