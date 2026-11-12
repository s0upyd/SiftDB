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

    private void SaveSettings(int id, JsonElement root)
    {
        if (_settings is null) { Error(id, "Settings cannot be saved here."); return; }
        var s = root.GetProperty("settings");
        var current = _settings.Get();
        var saved = _settings.Save(current with { NewSheetMode = OptionalString(s, "newSheetMode") ?? current.NewSheetMode });
        Event(new { type = "settings", replyTo = id, settings = SettingsOut(saved), error = (string?)null });
    }

    private static object SettingsOut(UserSettings s) => new { newSheetMode = s.NewSheetMode };
}
