using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiftDB.Engine;

/// <summary>
/// The user's settings (design section 10). Every later feature adds its settings here. Values are always valid: anything
/// missing or unrecognised in the file reads as the default.
/// </summary>
/// <param name="NewSheetMode">The mode new sheets open in (design section 3): "assisted" or "strict".</param>
public sealed record UserSettings(string NewSheetMode = "assisted")
{
    public static readonly UserSettings Default = new();

    /// <summary>Only the values this version knows are kept; anything else becomes the default.</summary>
    public UserSettings Normalised() => this with { NewSheetMode = NewSheetMode is "strict" ? "strict" : "assisted" };
}

/// <summary>
/// User settings in <c>%APPDATA%\SiftDB\settings.json</c>, owned by the engine so a future TUI gets the same ones. Like the
/// connection store: written atomically, settings a newer version added are kept on rewrite, and a file this version
/// cannot read is reported and never overwritten (the defaults apply meanwhile).
/// </summary>
public sealed class SettingsStore(string path)
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private UserSettings? _settings;
    private string? _loadError;
    private Dictionary<string, JsonElement>? _unknown;

    public static string DefaultPath => Path.Combine(ConnectionStore.DefaultDirectory, "settings.json");

    /// <summary>Why the file could not be read, or null. While set, <see cref="Get"/> gives the defaults and saving is refused.</summary>
    public string? LoadError
    {
        get { lock (_gate) { Loaded(); return _loadError; } }
    }

    public UserSettings Get()
    {
        lock (_gate) return Loaded();
    }

    public UserSettings Save(UserSettings settings)
    {
        lock (_gate)
        {
            Loaded();
            if (_loadError is not null) throw new InvalidDataException(_loadError);
            var normalised = settings.Normalised();
            var file = new StoreFile { Version = FormatVersion, NewSheetMode = normalised.NewSheetMode, Other = _unknown };
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, Json));
            _settings = normalised;
            return normalised;
        }
    }

    private UserSettings Loaded()
    {
        if (_settings is not null) return _settings;
        if (!File.Exists(path)) return _settings = UserSettings.Default;
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("The file is empty.");
            if (file.Version > FormatVersion)
                throw new InvalidDataException($"It was written by a newer SiftDB (format {file.Version}; this version reads {FormatVersion}).");
            _unknown = file.Other;
            return _settings = new UserSettings(file.NewSheetMode ?? UserSettings.Default.NewSheetMode).Normalised();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Never overwrite a file we cannot read; run on the defaults and say why.
            _loadError = $"Settings could not be read from {path}: {ex.Message} The file has been left as it is, and the defaults apply.";
            return _settings = UserSettings.Default;
        }
    }

    private sealed class StoreFile
    {
        public int Version { get; set; }
        public string? NewSheetMode { get; set; }

        /// <summary>Settings a later version added keep their values when this version rewrites the file.</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }
}
