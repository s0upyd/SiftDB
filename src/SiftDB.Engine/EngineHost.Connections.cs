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
    /// run; with none of those it tries without one. When the server wants one, or turns it down, the reply is
    /// "passwordNeeded" (<c>retry</c> true when one was tried): the UI asks, then connects again with what was typed.
    /// </summary>
    private async Task ConnectSavedAsync(int id, string savedId, string? typed)
    {
        var saved = Store.Get(savedId);
        if (saved is null) { Error(id, "That connection is no longer saved. Pick another one."); return; }
        if (!_providers.TryGetValue(saved.Provider, out var p)) { Error(id, $"{saved.Name} can't be opened. {MissingProvider(saved.Provider)}"); return; }

        // No secret needed (DynamoDB with a profile): connect without one, and never ask.
        var needed = NeedsSecret(p, saved.Fields);
        var password = !needed ? null
            : typed ?? (saved.RememberPassword ? Store.GetPassword(savedId) : null) ?? _promptedPasswords.GetValueOrDefault(savedId);
        var label = p.Secret?.Label ?? "Password";
        // With no password known, try without one first, and ask only when the server says it wants one: a server that
        // trusts this machine (pg_hba "trust", a .pgpass file) then needs no saved password and no prompt. Postgres gives
        // up before sending anything, so the server sees no failed password.

        try
        {
            var cs = p.BuildConnectionString(saved.Fields, password);
            if (!await ConnectAsync(id, saved.Provider, cs, new ConnectionOptions(saved.ReadOnly), savedId, saved.AutoTransaction)) return;
        }
        catch (ConnectionAuthenticationException ex)
        {
            _promptedPasswords.TryRemove(savedId, out _); // it was wrong: do not offer it to the next sheet either
            if (!needed) { Error(id, ex.Message); return; } // asking for a secret it doesn't use would not help
            Event(new { type = "passwordNeeded", replyTo = id, savedId, name = saved.Name, retry = password is not null, message = ex.Message, label });
            return;
        }
        if (typed is not null && !saved.RememberPassword) _promptedPasswords[savedId] = typed;
        Store.SetLastUsed(savedId);
    }

    /// <summary>
    /// Whether a connection with these fields uses the provider's secret (contract v14): never when it has none, else
    /// when its condition holds. A field left empty counts as its default.
    /// </summary>
    internal static bool NeedsSecret(IDatabaseProvider p, IReadOnlyDictionary<string, string> fields)
    {
        if (p.Secret is not { } spec) return false;
        if (spec.NeededWhen is not { } when) return true;
        var value = fields.GetValueOrDefault(when.Key) is { Length: > 0 } v ? v
            : p.ConnectionFields.FirstOrDefault(f => f.Key == when.Key)?.Default ?? "";
        return when.Values.Contains(value);
    }

    /// <summary>"password", "secret access key": the secret's name inside a sentence.</summary>
    private static string SecretWord(IDatabaseProvider p) =>
        p.Secret?.Label is { Length: > 0 } l ? char.ToLowerInvariant(l[0]) + l[1..] : "password";

    /// <summary>Why a saved connection of <paramref name="providerId"/> can't be used: its provider isn't there, or didn't load.</summary>
    private string MissingProvider(string providerId)
    {
        var r = _providerReports.LastOrDefault(r => string.Equals(r.Id, providerId, StringComparison.OrdinalIgnoreCase));
        return r is null
            ? $"It needs the '{providerId}' provider, which isn't installed."
            : $"It needs the {r.Name ?? r.Id} provider, which didn't load. {r.Reason}";
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
                secret = p.Secret,
            }),
            connections = store.List().Select(c => new
            {
                id = c.Id, name = c.Name, provider = c.Provider, fields = c.Fields, rememberPassword = c.RememberPassword,
                readOnly = c.ReadOnly, autoTransaction = c.AutoTransaction, hasPassword = c.RememberPassword && store.HasPassword(c.Id),
                // Its provider didn't load: the connection stays saved, untouched, and can't be opened or edited until it does (F18).
                unavailable = _providers.ContainsKey(c.Provider) ? null : MissingProvider(c.Provider),
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
        // A connection that no longer uses the secret (switched to an AWS profile) has its saved one removed: an unused
        // credential is not left on disk. Empty is the store's "remove".
        var password = NeedsSecret(p, input.Fields) ? OptionalString(root, "password") ?? PasswordFrom(p, root) : "";
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
        var password = FormPassword(p, root, input);
        var sw = Stopwatch.StartNew();
        try
        {
            var cs = p.BuildConnectionString(input.Fields, password);
            await using var conn = await p.OpenAsync(cs, new ConnectionOptions(input.ReadOnly), CancellationToken.None);
        }
        catch (ConnectionAuthenticationException ex)
        {
            Error(id, AuthRefused(p, input.Fields, password, ex));
            return;
        }
        Event(new { type = "ok", replyTo = id, ms = sw.ElapsedMilliseconds });
    }

    /// <summary>
    /// The server's databases, for the form's Database list: logged in with what the form holds, as Test is. The reply's
    /// "databases" is null when the provider cannot list them.
    /// </summary>
    private async Task ListDatabasesAsync(int id, JsonElement root)
    {
        var input = ReadConnection(root.GetProperty("connection"));
        if (!_providers.TryGetValue(input.Provider, out var p)) { Error(id, $"No provider '{input.Provider}'"); return; }
        var password = FormPassword(p, root, input);
        try
        {
            var databases = await p.ListDatabasesAsync(p.BuildConnectionString(input.Fields, password), CancellationToken.None);
            Event(new { type = "databases", replyTo = id, databases });
        }
        catch (ConnectionAuthenticationException ex)
        {
            Error(id, AuthRefused(p, input.Fields, password, ex));
        }
    }

    /// <summary>A login turned down, in the secret's own words; without a secret in play, the server's reason alone.</summary>
    private static string AuthRefused(IDatabaseProvider p, IReadOnlyDictionary<string, string> fields, string? secret, ConnectionAuthenticationException ex) =>
        !NeedsSecret(p, fields) ? ex.Message
        : secret is null ? $"The server wants a {SecretWord(p)}. {ex.Message}"
        : $"The server turned the {SecretWord(p)} down. {ex.Message}";

    /// <summary>
    /// The secret for a test or a database list: typed, else the pasted string's, else the one saved or typed earlier;
    /// none when the form's connection doesn't use one.
    /// </summary>
    private string? FormPassword(IDatabaseProvider p, JsonElement root, SavedConnection input) =>
        !NeedsSecret(p, input.Fields) ? null
        : OptionalString(root, "password") ?? PasswordFrom(p, root)
        ?? (input.Id.Length == 0 ? null : Store.GetPassword(input.Id) ?? _promptedPasswords.GetValueOrDefault(input.Id));

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
            c.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True,
            c.TryGetProperty("autoTransaction", out var at) && at.ValueKind == JsonValueKind.True);
    }

    private static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
