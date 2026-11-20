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

    /// <summary>The sizes the whole window can be drawn at, in percent (design section 10). 100 is the default.</summary>
    public static readonly IReadOnlyList<int> ZoomChoices = [100, 110, 125, 150, 175, 200];

    /// <summary>How large everything is drawn, in percent: text, icons and spacing grow together. One of <see cref="ZoomChoices"/>.</summary>
    public int Zoom { get; init; } = 100;

    /// <summary>Ask before running an UPDATE or DELETE with no WHERE, or a TRUNCATE (design section 14, F1). On by default.</summary>
    public bool WarnAllRows { get; init; } = true;

    /// <summary>The newest version whose What's New the user has had (design section 14, F17); null on a fresh install.</summary>
    public string? LastSeenVersion { get; init; }

    /// <summary>When What's New opens after an update: "majorMinor" (the default), "all" or "never".</summary>
    public string WhatsNew { get; init; } = "majorMinor";

    /// <summary>How Format SQL lays statements out (design section 14, F7).</summary>
    public FormatOptions Format { get; init; } = FormatOptions.Default;

    /// <summary>
    /// The command palette's recently used commands, by id, newest first (design section 14, F12). Ids this version does not
    /// have are kept: the UI skips them, and a later version may have them again.
    /// </summary>
    public IReadOnlyList<string> RecentCommands { get; init; } = [];

    /// <summary>Sheet files recently opened or saved, as full paths, newest first: the palette's "Open recent file…" (F12).</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    /// <summary>How many of each recent list are kept.</summary>
    public const int MaxRecentCommands = 10, MaxRecentFiles = 15;

    /// <summary>
    /// How file paths compare: ignoring case where the file system does (Windows, macOS by default), exactly elsewhere, where
    /// <c>a.sql</c> and <c>A.sql</c> are two files.
    /// </summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// <paramref name="list"/> with <paramref name="item"/> first and any earlier copy of it taken out, at most
    /// <paramref name="max"/> long. File paths compare with <see cref="PathComparer"/>, command ids exactly.
    /// </summary>
    public static IReadOnlyList<string> Remember(IReadOnlyList<string> list, string item, int max, StringComparer comparer) =>
        Recent([item, .. list], max, comparer);

    private static List<string> Recent(IEnumerable<string>? items, int max, StringComparer comparer) =>
        (items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(comparer).Take(max).ToList();

    /// <summary>The indent sizes offered, in spaces, and the line width's bounds.</summary>
    public static readonly IReadOnlyList<int> IndentChoices = [2, 4, 8];
    public const int MinLineWidth = 40, MaxLineWidth = 400;

    /// <summary>
    /// Only values this version knows are kept. <paramref name="strict"/>: a user rule that is not valid throws (saving)
    /// rather than being dropped (reading a file).
    /// </summary>
    public UserSettings Normalised(bool strict = false) => this with
    {
        NewSheetMode = NewSheetMode is "strict" ? "strict" : "assisted",
        ResultRules = StyleRules.Normalise(ResultRules, strict),
        Zoom = ZoomChoices.Contains(Zoom) ? Zoom : 100,
        WhatsNew = WhatsNew is "all" or "never" ? WhatsNew : "majorMinor",
        RecentCommands = Recent(RecentCommands, MaxRecentCommands, StringComparer.Ordinal),
        RecentFiles = Recent(RecentFiles, MaxRecentFiles, PathComparer),
        Format = (Format ?? FormatOptions.Default) with
        {
            KeywordCase = Enum.IsDefined(Format?.KeywordCase ?? KeywordCase.AsTyped) ? Format!.KeywordCase : KeywordCase.AsTyped,
            Commas = Enum.IsDefined(Format?.Commas ?? CommaPlacement.End) ? Format!.Commas : CommaPlacement.End,
            Indent = IndentChoices.Contains(Format?.Indent ?? 4) ? Format!.Indent : 4,
            LineWidth = Math.Clamp(Format?.LineWidth ?? 100, MinLineWidth, MaxLineWidth),
        },
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

    /// <summary>
    /// Whether this store's file was already there when the store was made, at startup: an earlier run used SiftDB. Taken
    /// then, before this run writes anything (autosave makes the session file within seconds, even on a fresh install).
    /// What's New uses it to tell a fresh install from an update from a version that recorded no "last seen" (F17).
    /// </summary>
    public bool ExistedAtStart { get; } = File.Exists(path);

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
                Zoom = normalised.Zoom,
                WarnAllRows = normalised.WarnAllRows,
                LastSeenVersion = normalised.LastSeenVersion,
                WhatsNew = normalised.WhatsNew,
                RecentCommands = normalised.RecentCommands.Count == 0 ? null : [.. normalised.RecentCommands],
                RecentFiles = normalised.RecentFiles.Count == 0 ? null : [.. normalised.RecentFiles],
                Format = new StoredFormat
                {
                    KeywordCase = normalised.Format.KeywordCase, Indent = normalised.Format.Indent,
                    Commas = normalised.Format.Commas, LineWidth = normalised.Format.LineWidth,
                },
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
            return _settings = new UserSettings(file.NewSheetMode ?? UserSettings.Default.NewSheetMode, rules.ToList(), providers)
                {
                    Zoom = file.Zoom ?? 100, WarnAllRows = file.WarnAllRows ?? true,
                    LastSeenVersion = file.LastSeenVersion, WhatsNew = file.WhatsNew ?? "majorMinor",
                    RecentCommands = file.RecentCommands ?? [], RecentFiles = file.RecentFiles ?? [],
                    Format = new FormatOptions(
                        file.Format?.KeywordCase ?? KeywordCase.AsTyped, file.Format?.Indent ?? 4,
                        file.Format?.Commas ?? CommaPlacement.End, file.Format?.LineWidth ?? 100),
                }.Normalised();
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
        public int? Zoom { get; set; }
        public bool? WarnAllRows { get; set; }
        public string? LastSeenVersion { get; set; }
        public string? WhatsNew { get; set; }
        public List<string>? RecentCommands { get; set; }
        public List<string>? RecentFiles { get; set; }
        public StoredFormat? Format { get; set; }
        public List<StoredRule>? ResultRules { get; set; }
        public Dictionary<string, Dictionary<string, string>>? ProviderSettings { get; set; }

        /// <summary>Settings a later version added keep their values when this version rewrites the file.</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    /// <summary>Format SQL's settings. A value missing, or one this version does not know, reads as the default.</summary>
    private sealed class StoredFormat
    {
        public KeywordCase? KeywordCase { get; set; }
        public int? Indent { get; set; }
        public CommaPlacement? Commas { get; set; }
        public int? LineWidth { get; set; }
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
