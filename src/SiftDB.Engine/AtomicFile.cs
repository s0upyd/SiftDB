namespace SiftDB.Engine;

/// <summary>Writes beside the target, then swaps it in, so readers see the old file or the new one, never a torn one.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string text) => Swap(path, temp => File.WriteAllText(temp, text));

    public static void WriteAllBytes(string path, byte[] bytes) => Swap(path, temp => File.WriteAllBytes(temp, bytes));

    private static void Swap(string path, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            write(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort: never hide the real failure */ }
            throw;
        }
    }
}
