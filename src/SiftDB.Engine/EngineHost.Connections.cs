using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// Saved connections (design section 8): list, save, delete, test, and connecting to one. Passwords come in from the UI
/// (the edit form, the connect prompt) and are never sent back out.
/// </summary>
public sealed partial class EngineHost
{
    private readonly ConnectionStore? _store;

    /// <summary>
    /// Passwords typed at the connect prompt for connections that do not remember theirs, by saved id. Memory only, for
    /// this run of the app, so a second sheet on the same connection does not ask again.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _promptedPasswords = new();

    private ConnectionStore Store => _store ?? throw new InvalidOperationException("Saved connections are not available here.");

    /// <summary>
    /// Opens a saved connection. The password is the one just typed, else the saved one, else one typed earlier in this
    /// run. When none is known and the connection does not remember its password, or the server turns the password down,
    /// the reply is "passwordNeeded": the UI asks, then connects again with what was typed.
    /// </summary>
    private async Task ConnectSavedAsync(int id, string savedId, string? typed)
    {
        var saved = Store.Get(savedId);
        if (saved is null) { Error(id, "That connection is no longer saved. Pick another one."); return; }
        if (!_providers.TryGetValue(saved.Provider, out var p)) { Error(id, $"No provider '{saved.Provider}' for '{saved.Name}'"); return; }

        var password = typed ?? (saved.RememberPassword ? Store.GetPassword(savedId) : null) ?? _promptedPasswords.GetValueOrDefault(savedId);
        if (password is null && !saved.RememberPassword)
        {
            Event(new { type = "passwordNeeded", replyTo = id, savedId, name = saved.Name, retry = false });
            return;
        }

        try
        {
            var cs = p.BuildConnectionString(saved.Fields, password);
            if (!await ConnectAsync(id, saved.Provider, cs, new ConnectionOptions(saved.ReadOnly), savedId)) return;
        }
        catch (ConnectionAuthenticationException ex)
        {
            _promptedPasswords.TryRemove(savedId, out _); // it was wrong: do not offer it to the next sheet either
            Event(new { type = "passwordNeeded", replyTo = id, savedId, name = saved.Name, retry = password is not null, message = ex.Message });
            return;
        }
        if (typed is not null && !saved.RememberPassword) _promptedPasswords[savedId] = typed;
        Store.SetLastUsed(savedId);
    }

    private void ListConnections(int id)
    {
        var store = Store;
        Event(new
        {
            type = "connections", replyTo = id,
            providers = _providers.Values.Select(p => new
            {
                id = p.Id, displayName = p.DisplayName, fields = p.ConnectionFields, readOnlyEnforcement = p.ReadOnlyEnforcement,
            }),
            connections = store.List().Select(c => new
            {
                id = c.Id, name = c.Name, provider = c.Provider, fields = c.Fields, rememberPassword = c.RememberPassword,
                readOnly = c.ReadOnly, hasPassword = c.RememberPassword && store.HasPassword(c.Id),
            }),
            lastUsed = store.LastUsed,
        });
    }

    /// <summary>
    /// "connection" is the form. "password" absent keeps the saved one. "fromConnectionString" is the pasted string a
    /// "Save as…" started from: its password is saved when none was typed.
    /// </summary>
    private void SaveConnection(int id, JsonElement root)
    {
        var input = ReadConnection(root.GetProperty("connection"));
        if (!_providers.TryGetValue(input.Provider, out var p)) { Error(id, $"No provider '{input.Provider}'"); return; }
        p.BuildConnectionString(input.Fields, null); // a bad port or SSL mode is reported now, not at the next connect
        var password = OptionalString(root, "password") ?? PasswordFrom(p, root);
        var saved = Store.Save(input, password);
        if (password is not null || !saved.RememberPassword) _promptedPasswords.TryRemove(saved.Id, out _);
        Event(new { type = "connectionSaved", replyTo = id, savedId = saved.Id });
    }

    /// <summary>Refused while a sheet uses it: those sheets would be left on a connection that no longer exists.</summary>
    private void DeleteConnection(int id, string savedId)
    {
        var inUse = _connections.Values.Count(s => s.SavedId == savedId);
        if (inUse > 0)
        {
            Error(id, inUse == 1
                ? "A sheet is connected with it. Close that sheet, or connect it elsewhere, first."
                : $"{inUse} sheets are connected with it. Close those sheets, or connect them elsewhere, first.");
            return;
        }
        Store.Delete(savedId);
        _promptedPasswords.TryRemove(savedId, out _);
        Event(new { type = "ok", replyTo = id });
    }

    /// <summary>Opens and closes a session with what the form holds, saved or not, with read-only applied.</summary>
    private async Task TestConnectionAsync(int id, JsonElement root)
    {
        var input = ReadConnection(root.GetProperty("connection"));
        if (!_providers.TryGetValue(input.Provider, out var p)) { Error(id, $"No provider '{input.Provider}'"); return; }
        var password = OptionalString(root, "password") ?? PasswordFrom(p, root)
            ?? (input.Id.Length == 0 ? null : Store.GetPassword(input.Id) ?? _promptedPasswords.GetValueOrDefault(input.Id));
        var sw = Stopwatch.StartNew();
        try
        {
            var cs = p.BuildConnectionString(input.Fields, password);
            await using var conn = await p.OpenAsync(cs, new ConnectionOptions(input.ReadOnly), CancellationToken.None);
        }
        catch (ConnectionAuthenticationException ex)
        {
            Error(id, password is null ? $"The server wants a password. {ex.Message}" : $"The server turned the password down. {ex.Message}");
            return;
        }
        Event(new { type = "ok", replyTo = id, ms = sw.ElapsedMilliseconds });
    }

    /// <summary>Takes a pasted connection string apart for "Save as…". Its password stays here: only whether it has one goes back.</summary>
    private void ParseConnectionString(int id, string provider, string connectionString)
    {
        if (!_providers.TryGetValue(provider, out var p)) { Error(id, $"No provider '{provider}'"); return; }
        var parsed = p.ParseConnectionString(connectionString);
        Event(new { type = "parsedConnection", replyTo = id, fields = parsed.Fields, hasPassword = parsed.Password is not null });
    }

    private static string? PasswordFrom(IDatabaseProvider p, JsonElement root) =>
        OptionalString(root, "fromConnectionString") is { } source ? p.ParseConnectionString(source).Password : null;

    private static SavedConnection ReadConnection(JsonElement c)
    {
        var fields = new Dictionary<string, string>();
        if (c.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var kv in f.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String) fields[kv.Name] = kv.Value.GetString()!;
        return new SavedConnection(
            OptionalString(c, "id") ?? "",
            OptionalString(c, "name") ?? "",
            OptionalString(c, "provider") ?? "",
            fields,
            c.TryGetProperty("rememberPassword", out var r) && r.ValueKind == JsonValueKind.True,
            c.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True);
    }

    private static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
