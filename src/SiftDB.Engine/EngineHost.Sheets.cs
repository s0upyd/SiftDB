using System.Text.Json;

namespace SiftDB.Engine;

/// <summary>
/// Sheet files and the session (design section 6): opening and saving <c>.sql</c> files, and autosaving the open sheets so
/// they come back after a restart. The engine does the file work so a future TUI behaves the same; choosing a file (the
/// native dialog) is the host's.
/// </summary>
public sealed partial class EngineHost
{
    private readonly SessionStore? _session;

    private SessionStore Session => _session ?? throw new InvalidOperationException("Autosave is not available here.");

    /// <summary>Reads a sheet file. The connection it names is matched to a saved one by name; an unknown name comes back unmatched.</summary>
    private void OpenFile(int id, string path)
    {
        var content = SheetFile.Read(path);
        var name = content.Header.Connection;
        var saved = name is null ? null : _store?.List().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        Event(new
        {
            type = "fileOpened", replyTo = id, path = Path.GetFullPath(path), fileName = Path.GetFileName(path),
            text = content.Text, mode = content.Header.Mode, connectionName = name, savedId = saved?.Id,
            lineEnding = content.LineEnding, encoding = content.Encoding, headerOther = PairsOut(content.Header.Other),
        });
    }

    /// <summary>
    /// Writes a sheet file. The header names the saved connection (by its current name), or keeps a name the file had that
    /// is not saved here, so the file's intent survives being opened and saved on another machine.
    /// </summary>
    private void SaveFile(int id, JsonElement root)
    {
        var path = root.GetProperty("path").GetString()!;
        var savedId = OptionalString(root, "savedId");
        var connection = savedId is null ? OptionalString(root, "connectionName") : _store?.Get(savedId)?.Name;
        var header = new SheetHeader(connection, OptionalString(root, "mode"), PairsIn(root, "headerOther"));
        SheetFile.Write(path, root.GetProperty("text").GetString()!, header,
            OptionalString(root, "lineEnding") ?? SheetFile.Crlf, OptionalString(root, "encoding") ?? "utf-8");
        Event(new { type = "fileSaved", replyTo = id, path = Path.GetFullPath(path), fileName = Path.GetFileName(path) });
    }

    /// <summary>
    /// The open sheets, in order. Each carries "text" only when it changed since the last call, so typing in one sheet does
    /// not rewrite all of them.
    /// </summary>
    private void SaveSession(int id, JsonElement root)
    {
        var sheets = new List<SessionSheet>();
        var texts = new Dictionary<string, string>();
        foreach (var s in root.GetProperty("sheets").EnumerateArray())
        {
            var sheet = new SessionSheet(
                s.GetProperty("id").GetString()!,
                OptionalString(s, "title") ?? "Sheet",
                OptionalString(s, "path"),
                s.TryGetProperty("dirty", out var d) && d.ValueKind == JsonValueKind.True,
                OptionalString(s, "savedId"),
                OptionalString(s, "connectionName"),
                OptionalString(s, "mode") ?? "assisted",
                OptionalString(s, "lineEnding") ?? SheetFile.Crlf,
                OptionalString(s, "encoding") ?? "utf-8",
                PairsIn(s, "headerOther"));
            sheets.Add(sheet);
            if (OptionalString(s, "text") is { } text) texts[sheet.Id] = text;
        }
        Session.Save(sheets, OptionalString(root, "active"), texts);
        Event(new { type = "ok", replyTo = id });
    }

    private void LoadSession(int id)
    {
        var (sheets, active) = Session.Load();
        Event(new
        {
            type = "session", replyTo = id, active,
            sheets = sheets.Select(r => new
            {
                id = r.Sheet.Id, title = r.Sheet.Title, path = r.Sheet.Path,
                fileName = r.Sheet.Path is null ? null : Path.GetFileName(r.Sheet.Path),
                dirty = r.Sheet.Dirty, missing = r.Missing,
                // A connection deleted since comes back unmatched, with the name it had, like an unknown name in a file.
                savedId = r.Sheet.SavedId is { } sid && _store?.Get(sid) is not null ? sid : null,
                connectionName = r.Sheet.ConnectionName,
                mode = r.Sheet.Mode, lineEnding = r.Sheet.LineEnding, encoding = r.Sheet.Encoding,
                headerOther = PairsOut(r.Sheet.HeaderOther), text = r.Text,
            }),
        });
    }

    private static string[][] PairsOut(IReadOnlyList<KeyValuePair<string, string>> pairs) =>
        pairs.Select(p => new[] { p.Key, p.Value }).ToArray();

    private static List<KeyValuePair<string, string>> PairsIn(JsonElement e, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in arr.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && p[0].GetString() is { } k && p[1].GetString() is { } v)
                list.Add(new(k, v));
        return list;
    }
}
