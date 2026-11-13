using System.Text;

namespace SiftDB.Engine;

/// <summary>
/// A result being exported to a file (design section 11, <i>Export</i>). The text arrives in pieces and is written to a
/// temporary file beside the target, which replaces the target only when the export finishes. So a failed or cancelled
/// export leaves no half-written file, and the file it would have replaced is untouched.
/// </summary>
internal sealed class ExportFile : IDisposable
{
    private readonly string _temp;
    private StreamWriter? _writer;

    public string Path { get; }

    /// <param name="byteOrderMark">Start the file with the UTF-8 byte-order mark: Excel needs it to read a CSV as UTF-8.</param>
    public ExportFile(string path, bool byteOrderMark)
    {
        Path = System.IO.Path.GetFullPath(path);
        _temp = Path + ".siftdb-export.tmp";
        var stream = new FileStream(_temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        _writer = new StreamWriter(stream, new UTF8Encoding(byteOrderMark));
    }

    public void Write(string text) => (_writer ?? throw new InvalidOperationException("The export has ended.")).Write(text);

    /// <summary>Puts the file in place, replacing what was there. Fails (and leaves the old file) if that file is open elsewhere.</summary>
    public void Finish()
    {
        var w = _writer ?? throw new InvalidOperationException("The export has ended.");
        _writer = null;
        try
        {
            w.Dispose();
            File.Move(_temp, Path, overwrite: true);
        }
        catch
        {
            DeleteTemp();
            throw;
        }
    }

    /// <summary>Throws the partial file away. Safe to call more than once, and after <see cref="Finish"/>.</summary>
    public void Dispose()
    {
        if (_writer is null) return;
        try { _writer.Dispose(); }
        catch (IOException) { /* a full disk fails the flush too; the file is being thrown away anyway */ }
        _writer = null;
        DeleteTemp();
    }

    private void DeleteTemp()
    {
        try { File.Delete(_temp); }
        catch (IOException) { /* best effort: never hide the real failure */ }
        catch (UnauthorizedAccessException) { }
    }
}
