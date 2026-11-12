using System.Text.Json;

namespace SiftDB.Engine;

/// <summary>User settings (design section 10): read them, and save the whole set from the settings editor.</summary>
public sealed partial class EngineHost
{
    private readonly SettingsStore? _settings;

    /// <summary>Without a store (tests, a host that keeps none) the defaults apply and saving is refused.</summary>
    private void GetSettings(int id)
    {
        var settings = _settings?.Get() ?? UserSettings.Default;
        Event(new { type = "settings", replyTo = id, settings = SettingsOut(settings), error = _settings?.LoadError });
    }

    /// <summary>A setting left out of the command keeps its current value. A rule that is not valid is refused with the reason.</summary>
    private void SaveSettings(int id, JsonElement root)
    {
        if (_settings is null) { Error(id, "Settings cannot be saved here."); return; }
        var s = root.GetProperty("settings");
        var current = _settings.Get();
        var next = current with
        {
            NewSheetMode = OptionalString(s, "newSheetMode") ?? current.NewSheetMode,
            ResultRules = s.TryGetProperty("resultRules", out var rules) && rules.ValueKind == JsonValueKind.Array
                ? rules.EnumerateArray().Select(ReadRule).ToList()
                : current.ResultRules,
        };
        var saved = _settings.Save(next);
        Event(new { type = "settings", replyTo = id, settings = SettingsOut(saved), error = (string?)null });
    }

    private static object SettingsOut(UserSettings s) => new
    {
        newSheetMode = s.NewSheetMode,
        resultRules = s.ResultRules.Select(r => new
        {
            id = r.Id, preset = r.IsPreset, name = r.Name, enabled = r.Enabled, columns = r.Columns, types = r.Types,
            condition = r.Condition, values = r.Values, text = r.Text, background = r.Background, bold = r.Bold,
        }),
    };

    private static StyleRule ReadRule(JsonElement r)
    {
        static List<string> Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];
        static bool Flag(JsonElement e, string name, bool fallback) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;
        return new StyleRule(
            OptionalString(r, "id") ?? "", OptionalString(r, "name") ?? "", Flag(r, "enabled", true), OptionalString(r, "columns") ?? "",
            StyleRules.ParseTypes(Strings(r, "types")), OptionalString(r, "condition") ?? "", Strings(r, "values"),
            OptionalString(r, "text") ?? "", OptionalString(r, "background") ?? "", Flag(r, "bold", false));
    }
}
