using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiftDB.Engine;

/// <summary>A saved connection (design section 8): one database. Its password, if remembered, is kept in the secret store.</summary>
/// <param name="Fields">The provider's fields, by key. Never holds a password.</param>
/// <param name="RememberPassword">False: connecting asks for the password (once per app run).</param>
public sealed record SavedConnection(
    string Id,
    string Name,
    string Provider,
    IReadOnlyDictionary<string, string> Fields,
    bool RememberPassword,
    bool ReadOnly);

/// <summary>Where saved passwords live. Values are secrets: never log them or send them to the UI.</summary>
public interface ISecretStore
{
    string? Get(string id);
    void Set(string id, string secret);
    void Remove(string id);
}

/// <summary>
/// Saved connections, in a JSON file that holds no secrets (so it could one day be shared or committed). Passwords go
/// to an <see cref="ISecretStore"/>. Every write replaces the file atomically, so a crash leaves the old or the new
/// version, never half of one. A file this version cannot read is never overwritten.
/// </summary>
public sealed class ConnectionStore
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly object _gate = new();
    private List<SavedConnection>? _connections;
    private string? _lastUsed;
    private string? _loadError;
    /// <summary>Per connection, settings a later version wrote that this one does not know: kept on rewrite.</summary>
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _unknown = new();

    public ConnectionStore(string path, ISecretStore secrets)
    {
        _path = path;
        _secrets = secrets;
    }

    /// <summary>The usual place: %APPDATA%\SiftDB.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SiftDB");

    public IReadOnlyList<SavedConnection> List()
    {
        lock (_gate) return [.. Loaded()];
    }

    public string? LastUsed
    {
        get { lock (_gate) { Loaded(); return _lastUsed; } }
    }

    public SavedConnection? Get(string id)
    {
        lock (_gate) return Loaded().FirstOrDefault(c => c.Id == id);
    }

    public bool HasPassword(string id) => _secrets.Get(id) is not null;

    public string? GetPassword(string id) => _secrets.Get(id);

    /// <summary>
    /// Adds or replaces a connection (a null or unknown id adds one). <paramref name="password"/> null keeps whatever is
    /// saved; a value replaces it. Not remembering the password removes any saved one.
    /// </summary>
    public SavedConnection Save(SavedConnection connection, string? password)
    {
        var name = connection.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the connection a name.");
        lock (_gate)
        {
            var list = Loaded();
            var id = string.IsNullOrEmpty(connection.Id) ? NewId() : connection.Id;
            if (list.Any(c => c.Id != id && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There is already a connection called '{name}'. Names must be unique: sheet files refer to connections by name.");

            var saved = connection with
            {
                Id = id,
                Name = name,
                Fields = connection.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key, f => f.Value.Trim()),
            };
            var at = list.FindIndex(c => c.Id == id);
            var updated = new List<SavedConnection>(list);
            if (at < 0) updated.Add(saved); else updated[at] = saved;
            Write(updated, _lastUsed);

            if (!saved.RememberPassword) _secrets.Remove(id);
            else if (password is not null)
            {
                if (password.Length == 0) _secrets.Remove(id); else _secrets.Set(id, password);
            }
            return saved;
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var list = Loaded();
            Write(list.Where(c => c.Id != id).ToList(), _lastUsed == id ? null : _lastUsed);
            _secrets.Remove(id);
        }
    }

    public void SetLastUsed(string id)
    {
        lock (_gate)
        {
            var list = Loaded();
            if (_lastUsed == id || list.All(c => c.Id != id)) return;
            Write(list, id);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private List<SavedConnection> Loaded()
    {
        if (_connections is not null) return _connections;
        if (_loadError is not null) throw new InvalidDataException(_loadError);
        if (!File.Exists(_path))
        {
            _connections = [];
            return _connections;
        }
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), Json)
                ?? throw new InvalidDataException("The file is empty.");
            if (file.Version > FormatVersion)
                throw new InvalidDataException($"It was written by a newer SiftDB (format {file.Version}; this version reads {FormatVersion}).");
            var stored = (file.Connections ?? [])
                .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Provider))
                .ToList();
            foreach (var c in stored) if (c.Other is { Count: > 0 } other) _unknown[c.Id!] = other;
            _connections = stored
                .Select(c => new SavedConnection(c.Id!, c.Name!, c.Provider!, c.Fields ?? new(), c.RememberPassword, c.ReadOnly))
                .ToList();
            _lastUsed = file.LastUsed;
            return _connections;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Never overwrite a file we cannot read: it may hold connections a newer version (or a careful hand) put there.
            _loadError = $"Saved connections could not be read from {_path}: {ex.Message} The file has been left as it is.";
            throw new InvalidDataException(_loadError);
        }
    }

    private void Write(List<SavedConnection> connections, string? lastUsed)
    {
        var file = new StoreFile
        {
            Version = FormatVersion,
            LastUsed = lastUsed,
            Connections = connections.Select(c => new StoredConnection
            {
                Id = c.Id, Name = c.Name, Provider = c.Provider, Fields = new Dictionary<string, string>(c.Fields),
                RememberPassword = c.RememberPassword, ReadOnly = c.ReadOnly,
                Other = _unknown.GetValueOrDefault(c.Id),
            }).ToList(),
        };
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, Json));
        _connections = connections;
        _lastUsed = lastUsed;
    }

    private sealed class StoreFile
    {
        public int Version { get; set; }
        public string? LastUsed { get; set; }
        public List<StoredConnection>? Connections { get; set; }
    }

    private sealed class StoredConnection
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Provider { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
        public bool RememberPassword { get; set; }
        public bool ReadOnly { get; set; }

        /// <summary>Settings a later version added keep their values when this version rewrites the file.</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }
}

internal static class AtomicFile
{
    /// <summary>Writes beside the target, then swaps it in, so readers see the old file or the new one, never a torn one.</summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
