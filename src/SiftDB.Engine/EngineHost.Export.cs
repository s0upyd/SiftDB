using System.Collections.Concurrent;
using System.Text.Json;

namespace SiftDB.Engine;

/// <summary>
/// Exporting a result to a file (design section 11, <i>Export</i>). The rows live in the front end (the engine lets a
/// statement go once it has streamed it), so the front end formats them and sends the text in pieces; the engine writes
/// the file, as it does for sheet files. A front end sends one piece at a time and waits for the answer, so the pieces
/// arrive in order and a large export never piles up in memory.
/// </summary>
public sealed partial class EngineHost
{
    private readonly ConcurrentDictionary<int, ExportFile> _exports = new();
    private int _nextExport;

    /// <summary>Commands: exportBegin {path, byteOrderMark} → exportStarted {exportId}; exportWrite {exportId, text} → ok;
    /// exportEnd {exportId, cancel} → exportFinished {path, fileName} (or ok when cancelled).</summary>
    private void ExportBegin(int id, JsonElement root)
    {
        var path = root.GetProperty("path").GetString()!;
        var bom = root.TryGetProperty("byteOrderMark", out var b) && b.ValueKind == JsonValueKind.True;
        var file = new ExportFile(path, bom);
        var exportId = Interlocked.Increment(ref _nextExport);
        _exports[exportId] = file;
        Event(new { type = "exportStarted", replyTo = id, exportId });
    }

    private void ExportWrite(int id, JsonElement root)
    {
        var exportId = root.GetProperty("exportId").GetInt32();
        if (!_exports.TryGetValue(exportId, out var file)) { Error(id, "That export has ended."); return; }
        try
        {
            file.Write(root.GetProperty("text").GetString()!);
        }
        catch
        {
            // A failed write (a full disk) ends the export: nothing more can make the file whole.
            if (_exports.TryRemove(exportId, out var f)) f.Dispose();
            throw;
        }
        Event(new { type = "ok", replyTo = id });
    }

    private void ExportEnd(int id, JsonElement root)
    {
        var exportId = root.GetProperty("exportId").GetInt32();
        if (!_exports.TryRemove(exportId, out var file)) { Error(id, "That export has ended."); return; }
        using (file)
        {
            if (root.TryGetProperty("cancel", out var c) && c.ValueKind == JsonValueKind.True)
            {
                Event(new { type = "ok", replyTo = id });
                return;
            }
            file.Finish();
        }
        Event(new { type = "exportFinished", replyTo = id, path = file.Path, fileName = Path.GetFileName(file.Path) });
    }

    private void AbandonExports()
    {
        foreach (var id in _exports.Keys)
            if (_exports.TryRemove(id, out var f)) f.Dispose();
    }
}
