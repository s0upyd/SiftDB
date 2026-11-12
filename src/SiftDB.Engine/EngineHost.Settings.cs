using System.Text.Json;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>User settings (design section 10): read them, and save the whole set from the settings editor.</summary>
public sealed partial class EngineHost
{
    private readonly SettingsStore? _settings;

    private UserSettings CurrentSettings => _settings?.Get() ?? UserSettings.Default;

    /// <summary>Without a store (tests, a host that keeps none) the defaults apply and saving is refused.</summary>
    private void GetSettings(int id) =>
        Event(new { type = "settings", replyTo = id, settings = SettingsOut(CurrentSettings), providers = ProviderSettingsOut(), error = _settings?.LoadError });

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
            ProviderSettings = s.TryGetProperty("providerSettings", out var ps) && ps.ValueKind == JsonValueKind.Object
                ? ReadProviderSettings(ps, current.ProviderSettings)
                : current.ProviderSettings,
        };
        var saved = _settings.Save(next);
        ApplyProviderSettings(saved);
        Event(new { type = "settings", replyTo = id, settings = SettingsOut(saved), providers = ProviderSettingsOut(), error = (string?)null });
    }

    /// <summary>Hands each loaded provider its values. At startup, and after every save, so open sessions follow at once.</summary>
    private void ApplyProviderSettings(UserSettings settings)
    {
        foreach (var p in _providers.Values)
            if (p.Settings.Count > 0) p.ApplySettings(EffectiveValues(p, settings));
    }

    /// <summary>One value per setting the provider describes: the stored one if it is still a choice, else the default.</summary>
    private static Dictionary<string, string> EffectiveValues(IDatabaseProvider p, UserSettings settings)
    {
        var stored = settings.ProviderSettings.GetValueOrDefault(p.Id);
        return p.Settings.ToDictionary(d => d.Key, d =>
            stored?.GetValueOrDefault(d.Key) is { } v && d.Choices.Any(c => c.Value == v) ? v : d.Default);
    }

    /// <summary>
    /// The providers' values from the editor, over the stored ones. Each provider sent replaces its own set; providers not
    /// sent (or not loaded) keep theirs. A value that is not one of the setting's choices is refused.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadProviderSettings(
        JsonElement sent, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> current)
    {
        var result = current.ToDictionary(p => p.Key, p => p.Value);
        foreach (var provider in sent.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object) continue;
            _providers.TryGetValue(provider.Name, out var p);
            var values = new Dictionary<string, string>();
            foreach (var kv in provider.Value.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.String) continue;
                var value = kv.Value.GetString()!;
                var setting = p?.Settings.FirstOrDefault(d => d.Key == kv.Name);
                if (p is not null && setting is null) continue; // a setting this provider no longer has
                if (setting is not null && setting.Choices.All(c => c.Value != value))
                    throw new ArgumentException($"'{value}' is not a choice for \"{setting.Label}\".");
                values[kv.Name] = value;
            }
            result[provider.Name] = values;
        }
        return result;
    }

    private object ProviderSettingsOut() => _providers.Values.Where(p => p.Settings.Count > 0).Select(p => new
    {
        id = p.Id, displayName = p.DisplayName,
        settings = p.Settings.Select(d => new
        {
            key = d.Key, label = d.Label, help = d.Help, @default = d.Default,
            choices = d.Choices.Select(c => new { value = c.Value, label = c.Label, help = c.Help }),
        }),
        values = EffectiveValues(p, CurrentSettings),
    });

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
