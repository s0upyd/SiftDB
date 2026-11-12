using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiftDB.Engine;

/// <summary>One open sheet as the session remembers it (design section 6). Never holds a pasted connection string.</summary>
/// <param name="Id">Stable for the sheet's life, and the name of its autosaved text.</param>
/// <param name="Title">An untitled sheet's name ("Sheet 3"); a file's title comes from its name.</param>
/// <param name="Path">The file it was opened from or saved to, or null.</param>
/// <param name="Dirty">Has edits that are not in its file (always true for an untitled sheet with text).</param>
/// <param name="SavedId">The saved connection chosen for it, or null (a pasted string is not kept: it may hold a password).</param>
/// <param name="ConnectionName">A connection a file named that is not saved here, so the sheet can still say so after a restart.</param>
public sealed record SessionSheet(
    string Id,
    string Title,
    string? Path,
    bool Dirty,
    string? SavedId,
    string? ConnectionName,
    string Mode,
    string LineEnding,
    string Encoding,
    IReadOnlyList<KeyValuePair<string, string>> HeaderOther);

/// <summary>A sheet coming back after a restart: its remembered state and its text.</summary>
/// <param name="Missing">Its file has gone: the autosaved text is all there is, so it comes back as unsaved.</param>
public sealed record RestoredSheet(SessionSheet Sheet, string Text, bool Missing);

/// <summary>
/// Autosave and restore (design section 6): the open sheets, in order, and each one's text, in <c>%APPDATA%\SiftDB\session</c>.
/// Every sheet is kept, saved or not, so closing the window loses nothing. A sheet whose file holds all its text comes back
/// from the file (it may have changed since); one with unsaved edits comes back from its autosaved copy. Writes are atomic.
/// </summary>
public sealed class SessionStore(string directory)
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public static string DefaultDirectory => Path.Combine(ConnectionStore.DefaultDirectory, "session");

    private string ManifestPath => Path.Combine(directory, "session.json");
    private string TextPath(string id) => Path.Combine(directory, "sheets", $"{id}.sql");

    /// <summary>
    /// Records the open sheets. <paramref name="texts"/> holds only the sheets whose text changed since the last save
    /// (by id); the others keep what was written before. Texts of sheets no longer open are deleted.
    /// </summary>
    public void Save(IReadOnlyList<SessionSheet> sheets, string? activeId, IReadOnlyDictionary<string, string> texts)
    {
        foreach (var s in sheets) CheckId(s.Id);
        lock (_gate)
        {
            foreach (var (id, text) in texts)
                if (sheets.Any(s => s.Id == id)) AtomicFile.WriteAllText(TextPath(id), text);

            var file = new Manifest
            {
                Version = FormatVersion,
                Active = activeId,
                Sheets = sheets.Select(s => new StoredSheet
                {
                    Id = s.Id, Title = s.Title, Path = s.Path, Dirty = s.Dirty, SavedId = s.SavedId, ConnectionName = s.ConnectionName,
                    Mode = s.Mode, LineEnding = s.LineEnding, Encoding = s.Encoding,
                    HeaderOther = s.HeaderOther.Select(kv => new[] { kv.Key, kv.Value }).ToList(),
                }).ToList(),
            };
            AtomicFile.WriteAllText(ManifestPath, JsonSerializer.Serialize(file, Json));

            var keep = sheets.Select(s => s.Id).ToHashSet();
            var textDir = Path.Combine(directory, "sheets");
            if (Directory.Exists(textDir))
                foreach (var f in Directory.EnumerateFiles(textDir, "*.sql"))
                    if (!keep.Contains(Path.GetFileNameWithoutExtension(f))) File.Delete(f);
        }
    }

    /// <summary>The sheets to bring back, in order, and which was active. Empty when there is nothing (or nothing readable).</summary>
    public (IReadOnlyList<RestoredSheet> Sheets, string? ActiveId) Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ManifestPath)) return ([], null);
            Manifest? file;
            try { file = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), Json); }
            catch (JsonException) { return ([], null); } // a broken session is not worth failing startup for
            if (file is null || file.Version > FormatVersion) return ([], null);

            var restored = new List<RestoredSheet>();
            foreach (var s in file.Sheets ?? [])
            {
                if (string.IsNullOrEmpty(s.Id) || !IsSafeId(s.Id)) continue;
                var sheet = new SessionSheet(s.Id, s.Title ?? "Sheet", s.Path, s.Dirty, s.SavedId, s.ConnectionName,
                    s.Mode is "strict" ? "strict" : "assisted", s.LineEnding is SheetFile.Lf ? SheetFile.Lf : SheetFile.Crlf,
                    s.Encoding ?? "utf-8",
                    (s.HeaderOther ?? []).Where(p => p.Length == 2).Select(p => new KeyValuePair<string, string>(p[0], p[1])).ToList());
                var autosaved = File.Exists(TextPath(s.Id)) ? File.ReadAllText(TextPath(s.Id)) : null;

                // A sheet with nothing unsaved is its file: read it afresh, since it may have changed while SiftDB was closed.
                if (!s.Dirty && s.Path is not null)
                {
                    if (TryReadFile(s.Path) is { } content)
                    {
                        restored.Add(new RestoredSheet(sheet with { LineEnding = content.LineEnding, Encoding = content.Encoding, HeaderOther = content.Header.Other },
                            content.Text, Missing: false));
                        continue;
                    }
                    restored.Add(new RestoredSheet(sheet with { Dirty = true }, autosaved ?? "", Missing: true));
                    continue;
                }
                restored.Add(new RestoredSheet(sheet, autosaved ?? "", Missing: s.Path is not null && !File.Exists(s.Path)));
            }
            return (restored, file.Active);
        }
    }

    private static SheetFileContent? TryReadFile(string path)
    {
        try { return File.Exists(path) ? SheetFile.Read(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>Ids name files, so they must not be able to point anywhere else.</summary>
    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void CheckId(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException($"'{id}' is not a valid sheet id.");
    }

    private sealed class Manifest
    {
        public int Version { get; set; }
        public string? Active { get; set; }
        public List<StoredSheet>? Sheets { get; set; }
    }

    private sealed class StoredSheet
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Path { get; set; }
        public bool Dirty { get; set; }
        public string? SavedId { get; set; }
        public string? ConnectionName { get; set; }
        public string? Mode { get; set; }
        public string? LineEnding { get; set; }
        public string? Encoding { get; set; }
        public List<string[]>? HeaderOther { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
