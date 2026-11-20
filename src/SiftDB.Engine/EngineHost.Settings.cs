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
            Zoom = s.TryGetProperty("zoom", out var zoom) && zoom.ValueKind == JsonValueKind.Number && zoom.TryGetInt32(out var z)
                ? z : current.Zoom,
            WhatsNew = OptionalString(s, "whatsNew") ?? current.WhatsNew,
            Format = s.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object ? ReadFormat(fmt, current.Format) : current.Format,
            WarnAllRows = s.TryGetProperty("warnAllRows", out var warn) && warn.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? warn.GetBoolean() : current.WarnAllRows,
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
        zoom = s.Zoom,
        zoomChoices = UserSettings.ZoomChoices,
        warnAllRows = s.WarnAllRows,
        whatsNew = s.WhatsNew,
        format = new
        {
            keywordCase = JsonNamingPolicy.CamelCase.ConvertName(s.Format.KeywordCase.ToString()),
            indent = s.Format.Indent,
            commas = JsonNamingPolicy.CamelCase.ConvertName(s.Format.Commas.ToString()),
            lineWidth = s.Format.LineWidth,
        },
        indentChoices = UserSettings.IndentChoices,
        lineWidthMin = UserSettings.MinLineWidth,
        lineWidthMax = UserSettings.MaxLineWidth,
        resultRules = s.ResultRules.Select(r => new
        {
            id = r.Id, preset = r.IsPreset, name = r.Name, enabled = r.Enabled, columns = r.Columns, types = r.Types,
            condition = r.Condition, values = r.Values, text = r.Text, background = r.Background, bold = r.Bold,
        }),
    };

    // ---- recently used, for the command palette (design section 14, F12) ----------------------------------------------
    // Kept in the settings file, so they survive a restart, but never part of the settings editor: its saves leave them be.

    /// <summary>"recent": the recently used commands, and the recent files that still exist, newest first.</summary>
    private void Recent(int id)
    {
        var s = CurrentSettings;
        Event(new { type = "recent", replyTo = id, commands = s.RecentCommands, files = s.RecentFiles.Where(File.Exists) });
    }

    /// <summary>"rememberCommand": a command was run from the palette. Answered like "recent".</summary>
    private void RememberCommand(int id, string commandId)
    {
        Remember(s => s with { RecentCommands = UserSettings.Remember(s.RecentCommands, commandId, UserSettings.MaxRecentCommands, StringComparer.Ordinal) });
        Recent(id);
    }

    /// <summary>A sheet file was opened or saved. Not being able to record it never fails the open or the save.</summary>
    private void RememberFile(string path)
    {
        var full = Path.GetFullPath(path);
        Remember(s => s with { RecentFiles = UserSettings.Remember(s.RecentFiles, full, UserSettings.MaxRecentFiles, UserSettings.PathComparer) });
    }

    private void Remember(Func<UserSettings, UserSettings> change)
    {
        if (_settings is not { LoadError: null }) return; // no store, or a file that must not be overwritten
        try { _settings.Save(change(_settings.Get())); }
        catch (IOException) { /* best effort: the list is a convenience */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// "whatsNew" (design section 14, F17), at startup with the running version: what to show (the window, a notice, or
    /// nothing), with the releases since the version last seen, which then becomes this one, so it never shows twice. With
    /// "all" (What's New opened by hand) every release this build has notes for, and nothing is recorded.
    /// </summary>
    private void WhatsNew(int id, string version, bool all)
    {
        if (all)
        {
            Event(new { type = "whatsNew", replyTo = id, show = "window", releases = ReleaseNotes.All, summary = (string?)null });
            return;
        }
        var current = CurrentSettings;
        // Something an earlier run left behind means this is not a fresh install, even with no version recorded yet.
        var returning = (_settings?.ExistedAtStart ?? false) || (_store?.ExistedAtStart ?? false) || (_session?.ExistedAtStart ?? false);
        var decision = ReleaseNotes.Decide(version, current.LastSeenVersion, current.WhatsNew, ReleaseNotes.All, returning);
        // Recorded on a fresh install too (nothing to show then), and never moved back by an older build.
        if (_settings is { LoadError: null } && (current.LastSeenVersion is null || ReleaseNotes.Compare(version, current.LastSeenVersion) > 0))
            _settings.Save(current with { LastSeenVersion = version });
        Event(new
        {
            type = "whatsNew", replyTo = id,
            show = decision.Show switch { WhatsNewShow.Window => "window", WhatsNewShow.Notice => "notice", _ => "none" },
            releases = decision.Releases, summary = decision.Summary,
        });
    }

    /// <summary>Format SQL's settings from the editor; anything left out keeps its value, anything unknown reads as the default.</summary>
    private static FormatOptions ReadFormat(JsonElement f, FormatOptions current) => new(
        OptionalString(f, "keywordCase") is { } k ? (Enum.TryParse<KeywordCase>(k, ignoreCase: true, out var kc) ? kc : KeywordCase.AsTyped) : current.KeywordCase,
        f.TryGetProperty("indent", out var i) && i.TryGetInt32(out var indent) ? indent : current.Indent,
        OptionalString(f, "commas") is { } c ? (Enum.TryParse<CommaPlacement>(c, ignoreCase: true, out var cp) ? cp : CommaPlacement.End) : current.Commas,
        f.TryGetProperty("lineWidth", out var w) && w.TryGetInt32(out var width) ? width : current.LineWidth);

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
