using System.Text;
using System.Text.RegularExpressions;

namespace SiftDB.Engine;

/// <summary>The settings line at the top of a sheet file: <c>-- db: connection=dev-pg; mode=assisted</c> (design section 6).</summary>
/// <param name="Connection">A saved connection's name, or null.</param>
/// <param name="Mode">"assisted" or "strict", or null.</param>
/// <param name="Other">Keys this version does not know (e.g. a later <c>vars=</c>), kept in order and written back as they were.</param>
public sealed record SheetHeader(string? Connection, string? Mode, IReadOnlyList<KeyValuePair<string, string>> Other)
{
    public static readonly SheetHeader Empty = new(null, null, []);
}

/// <summary>A sheet file as the editor sees it: the header taken off, and line endings as plain "\n".</summary>
/// <param name="LineEnding">"crlf" or "lf": what the file used, so saving writes it back the same way.</param>
/// <param name="Encoding">"utf-8", "utf-8-bom", "utf-16le" or "utf-16be": likewise.</param>
public sealed record SheetFileContent(string Text, SheetHeader Header, string LineEnding, string Encoding);

/// <summary>
/// Reads and writes plain <c>.sql</c> sheet files (design section 6). The header line belongs to SiftDB: it is taken off
/// when a file is read and written back on save, so it never shows in the editor. Everything else in the file is the
/// user's, kept as it was: other comments, the encoding, the line endings. A file that is not valid text in its encoding
/// is refused rather than read with replacement characters, which saving would then write back as damage.
/// </summary>
public static partial class SheetFile
{
    public const string Crlf = "crlf", Lf = "lf";

    [GeneratedRegex(@"^[ \t]*--[ \t]*db:[ \t]*(.*?)[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();

    public static SheetFileContent Read(string path) => Parse(File.ReadAllBytes(path));

    public static SheetFileContent Parse(byte[] bytes)
    {
        var (encodingName, text) = Decode(bytes);
        var lineEnding = DetectLineEnding(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var firstEnd = text.IndexOf('\n');
        var first = firstEnd < 0 ? text : text[..firstEnd];
        var header = SheetHeader.Empty;
        if (HeaderLine().Match(first) is { Success: true } m)
        {
            header = ParseHeader(m.Groups[1].Value);
            text = firstEnd < 0 ? "" : text[(firstEnd + 1)..];
        }
        return new SheetFileContent(text, header, lineEnding, encodingName);
    }

    /// <summary>Writes the file atomically: the header line (when there is anything to say), then the text.</summary>
    public static void Write(string path, string text, SheetHeader header, string lineEnding, string encoding) =>
        AtomicFile.WriteAllBytes(path, Compose(text, header, lineEnding, encoding));

    public static byte[] Compose(string text, SheetHeader header, string lineEnding, string encoding)
    {
        var body = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var line = FormatHeader(header);
        if (line is not null) body = line + "\n" + body;
        if (lineEnding == Crlf) body = body.Replace("\n", "\r\n");
        var enc = EncodingFor(encoding);
        return [.. enc.GetPreamble(), .. enc.GetBytes(body)];
    }

    // ---- the header line ----------------------------------------------------------------------------------------

    /// <summary><c>key=value; key="value; with semicolon"</c>. Keys are case-insensitive; a quote inside quotes is doubled.</summary>
    public static SheetHeader ParseHeader(string content)
    {
        string? connection = null, mode = null;
        var other = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in Pairs(content))
        {
            switch (key.ToLowerInvariant())
            {
                case "connection": connection = value; break;
                case "mode": mode = value.ToLowerInvariant() is "strict" ? "strict" : value.ToLowerInvariant() is "assisted" ? "assisted" : null; break;
                default: other.Add(new(key, value)); break;
            }
        }
        return new SheetHeader(string.IsNullOrEmpty(connection) ? null : connection, mode, other);
    }

    /// <summary>The header line, or null when there is nothing to put in it.</summary>
    public static string? FormatHeader(SheetHeader header)
    {
        var pairs = new List<string>();
        if (!string.IsNullOrEmpty(header.Connection)) pairs.Add($"connection={Quote(header.Connection)}");
        if (!string.IsNullOrEmpty(header.Mode)) pairs.Add($"mode={Quote(header.Mode)}");
        foreach (var (k, v) in header.Other) pairs.Add($"{k}={Quote(v)}");
        return pairs.Count == 0 ? null : "-- db: " + string.Join("; ", pairs);
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.IndexOfAny([';', '"', '=']) >= 0 || value.Trim().Length != value.Length
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static IEnumerable<(string Key, string Value)> Pairs(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var eq = s.IndexOf('=', i);
            var semi = s.IndexOf(';', i);
            if (eq < 0 || (semi >= 0 && semi < eq)) { i = semi < 0 ? s.Length : semi + 1; continue; } // a part with no '=': skip it
            var key = s[i..eq].Trim();
            i = eq + 1;
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            string value;
            if (i < s.Length && s[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                value = sb.ToString();
                var next = s.IndexOf(';', i);
                i = next < 0 ? s.Length : next + 1;
            }
            else
            {
                var next = s.IndexOf(';', i);
                value = (next < 0 ? s[i..] : s[i..next]).Trim();
                i = next < 0 ? s.Length : next + 1;
            }
            if (key.Length > 0) yield return (key, value);
        }
    }

    // ---- encoding and line endings ------------------------------------------------------------------------------

    private static (string Name, string Text) Decode(byte[] b)
    {
        string name;
        int skip;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) (name, skip) = ("utf-8-bom", 3);
        else if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) (name, skip) = ("utf-16le", 2);
        else if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) (name, skip) = ("utf-16be", 2);
        else (name, skip) = ("utf-8", 0);
        try
        {
            return (name, StrictEncodingFor(name).GetString(b, skip, b.Length - skip));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException(name.StartsWith("utf-8", StringComparison.Ordinal)
                ? "This file is not UTF-8 text (it may use an older Windows code page). SiftDB reads and writes UTF-8: convert the file first, so saving cannot damage it."
                : $"This file is not valid {name.ToUpperInvariant()} text.");
        }
    }

    private static Encoding StrictEncodingFor(string name) => name switch
    {
        "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
    };

    /// <summary>For writing: the preamble (byte-order mark) only when the file had one.</summary>
    private static Encoding EncodingFor(string name) => name switch
    {
        "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    /// <summary>Whichever the file uses most. A file with no line breaks gets the Windows convention.</summary>
    private static string DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        return lf > crlf ? Lf : Crlf;
    }
}
