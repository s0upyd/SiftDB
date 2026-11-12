using System.Text.Json;
using System.Text.Json.Serialization;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// The user's settings (design section 10). Every later feature adds its settings here. Values are always valid: anything
/// missing or unrecognised in the file reads as the default.
/// </summary>
/// <param name="NewSheetMode">The mode new sheets open in (design section 3): "assisted" or "strict".</param>
/// <param name="ResultRules">Result styling, as one ordered list: the first rule that matches styles the cell.</param>
/// <param name="ProviderSettings">Settings only one kind of database has, by provider id, then setting key. Kept as
/// stored: the engine checks them against each loaded provider's own descriptions when it applies them, so the settings
/// of a provider that is not loaded survive.</param>
public sealed record UserSettings(
    string NewSheetMode,
    IReadOnlyList<StyleRule> ResultRules,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ProviderSettings)
{
    public static readonly UserSettings Default = new("assisted", StyleRules.Default, new Dictionary<string, IReadOnlyDictionary<string, string>>());

    /// <summary>
    /// Only values this version knows are kept. <paramref name="strict"/>: a user rule that is not valid throws (saving)
    /// rather than being dropped (reading a file).
    /// </summary>
    public UserSettings Normalised(bool strict = false) => this with
    {
        NewSheetMode = NewSheetMode is "strict" ? "strict" : "assisted",
        ResultRules = StyleRules.Normalise(ResultRules, strict),
    };
}

/// <summary>
/// User settings in <c>%APPDATA%\SiftDB\settings.json</c>, owned by the engine so a future TUI gets the same ones. Like the
/// connection store: written atomically, settings a newer version added are kept on rewrite, and a file this version
/// cannot read is reported and never overwritten (the defaults apply meanwhile).
/// </summary>
public sealed class SettingsStore(string path)
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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

    /// <summary>Saves the whole set. A rule that is not valid is refused (ArgumentException) with the reason, and nothing is written.</summary>
    public UserSettings Save(UserSettings settings)
    {
        lock (_gate)
        {
            Loaded();
            if (_loadError is not null) throw new InvalidDataException(_loadError);
            var normalised = settings.Normalised(strict: true);
            var file = new StoreFile
            {
                Version = FormatVersion,
                NewSheetMode = normalised.NewSheetMode,
                ResultRules = normalised.ResultRules.Select(StoredRule.From).ToList(),
                ProviderSettings = normalised.ProviderSettings.Count == 0 ? null
                    : normalised.ProviderSettings.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value)),
                Other = _unknown,
            };
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
            var rules = file.ResultRules?.Where(r => r is not null).Select(r => r.ToRule()) ?? StyleRules.Default;
            var providers = (file.ProviderSettings ?? []).Where(p => p.Value is not null)
                .ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value);
            return _settings = new UserSettings(file.NewSheetMode ?? UserSettings.Default.NewSheetMode, rules.ToList(), providers).Normalised();
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
        public List<StoredRule>? ResultRules { get; set; }
        public Dictionary<string, Dictionary<string, string>>? ProviderSettings { get; set; }

        /// <summary>Settings a later version added keep their values when this version rewrites the file.</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    /// <summary>A preset is stored as its id and whether it is on; its definition comes from this version of the app.</summary>
    private sealed class StoredRule
    {
        public string? Id { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Name { get; set; }
        public string? Columns { get; set; }
        public List<string>? Types { get; set; }
        public string? Condition { get; set; }
        public List<string>? Values { get; set; }
        public string? Text { get; set; }
        public string? Background { get; set; }
        public bool? Bold { get; set; }

        public static StoredRule From(StyleRule r) => r.IsPreset
            ? new StoredRule { Id = r.Id, Enabled = r.Enabled }
            : new StoredRule
            {
                Id = r.Id, Enabled = r.Enabled, Name = r.Name, Columns = r.Columns,
                Types = r.Types.Select(t => JsonNamingPolicy.CamelCase.ConvertName(t.ToString())).ToList(),
                Condition = r.Condition, Values = [.. r.Values], Text = r.Text, Background = r.Background, Bold = r.Bold,
            };

        public StyleRule ToRule() => new(
            Id ?? "", Name ?? "", Enabled, Columns ?? "", StyleRules.ParseTypes(Types), Condition ?? "", Values ?? [],
            Text ?? "", Background ?? "", Bold ?? false);
    }
}
