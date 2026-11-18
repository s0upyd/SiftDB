using System.Text.RegularExpressions;

namespace SiftDB.Engine;

/// <summary>One entry of a release's notes. <paramref name="TryIt"/> is the app command its "Try it" runs, if it has one.</summary>
public sealed record ReleaseEntry(string Text, string? TryIt);

public sealed record ReleaseSection(string Title, IReadOnlyList<ReleaseEntry> Entries);

public sealed record Release(string Version, string? Date, IReadOnlyList<ReleaseSection> Sections);

/// <summary>What to show after an update (design section 14, F17).</summary>
public enum WhatsNewShow { None, Notice, Window }

/// <param name="Releases">The releases to show, newest first: every one since the version last seen.</param>
/// <param name="Summary">For the notice: "3 fixes", "2 improvements and a fix".</param>
public sealed record WhatsNewDecision(WhatsNewShow Show, IReadOnlyList<Release> Releases, string? Summary);

/// <summary>
/// The release notes the app ships with (design section 14, F17): CHANGELOG.md as git-cliff writes it in the release
/// workflow (cliff.toml), embedded in the engine, so What's New works offline and shows exactly what was shipped. A build
/// made without it (a local one) simply has no notes.
/// </summary>
public static partial class ReleaseNotes
{
    private static readonly Lazy<IReadOnlyList<Release>> Shipped = new(() =>
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("SiftDB.CHANGELOG.md");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The notes this build ships with, newest first.</summary>
    public static IReadOnlyList<Release> All => Shipped.Value;

    [GeneratedRegex(@"^##\s+\[?v?(?<version>[0-9][^\]\s]*)\]?(?:\s+-\s+(?<date>\S+))?\s*$")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"\s*\(\[Try it\]\(command:(?<id>[\w.\-]+)\)\)\s*$")]
    private static partial Regex TryItLink();

    /// <summary>
    /// Reads the changelog's layout: "## version - date", "### group", "- entry" (an entry may end in "([Try it](command:id))").
    /// "## Unreleased" and anything else is skipped.
    /// </summary>
    public static IReadOnlyList<Release> Parse(string markdown)
    {
        var releases = new List<Release>();
        string? version = null, date = null, section = null;
        var sections = new List<ReleaseSection>();
        var entries = new List<ReleaseEntry>();

        void EndSection()
        {
            if (section is not null && entries.Count > 0) sections.Add(new ReleaseSection(section, entries));
            section = null;
            entries = [];
        }
        void EndRelease()
        {
            EndSection();
            if (version is not null && sections.Count > 0) releases.Add(new Release(version, date, sections));
            version = null;
            sections = [];
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                EndRelease();
                if (VersionLine().Match(line) is { Success: true } m) { version = m.Groups["version"].Value; date = m.Groups["date"].Success ? m.Groups["date"].Value : null; }
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                EndSection();
                if (version is not null) section = line[4..].Trim();
            }
            else if (section is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                var text = line[2..].Trim();
                string? tryIt = null;
                if (TryItLink().Match(text) is { Success: true } t) { tryIt = t.Groups["id"].Value; text = text[..t.Index].TrimEnd(); }
                if (text.Length > 0) entries.Add(new ReleaseEntry(text, tryIt));
            }
        }
        EndRelease();
        releases.Sort((a, b) => Compare(b.Version, a.Version));
        return releases;
    }

    /// <summary>Semantic-version order: 0.10.0 after 0.9.1, and 0.2.0-beta.1 before 0.2.0. Not a version: before everything.</summary>
    public static int Compare(string a, string b)
    {
        var (ca, pa) = Split(a);
        var (cb, pb) = Split(b);
        if (ca is null || cb is null) return ca is null ? (cb is null ? 0 : -1) : 1;
        var c = ca.CompareTo(cb);
        if (c != 0) return c;
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;  // 0.2.0 comes after every 0.2.0-something
        if (pb.Length == 0) return -1;
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var x);
            var nb = int.TryParse(pb[i], out var y);
            c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    private static (Version? Core, string[] Pre) Split(string v)
    {
        v = v.Trim().TrimStart('v');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus]; // build metadata does not order
        var dash = v.IndexOf('-');
        var core = dash < 0 ? v : v[..dash];
        var pre = dash < 0 ? [] : v[(dash + 1)..].Split('.');
        return (Version.TryParse(core.Count(ch => ch == '.') == 1 ? core + ".0" : core, out var parsed) ? parsed : null, pre);
    }

    /// <summary>
    /// What to show at startup, running <paramref name="current"/>, when the user last saw <paramref name="lastSeen"/>.
    /// A fresh install (nothing seen yet, nothing left by an earlier run) shows nothing. A major or minor update opens What's New with every release since
    /// the last one seen; an update with only fixes says so in a small notice (or opens it too, with "all updates"). Nothing
    /// for a version that is not newer, nor when there are no notes for it (a local build).
    /// </summary>
    /// <param name="mode">"majorMinor" (the default), "all" or "never".</param>
    /// <param name="returningUser">With nothing seen yet: an earlier version was used here (it left a session, connections
    /// or settings behind), so this is an update from a version older than What's New, which recorded nothing. Then the
    /// release just installed is shown. Without it, nothing seen means a fresh install, and nothing is shown.</param>
    public static WhatsNewDecision Decide(string current, string? lastSeen, string mode, IReadOnlyList<Release> releases, bool returningUser = false)
    {
        var none = new WhatsNewDecision(WhatsNewShow.None, [], null);
        if (mode == "never") return none;
        if (lastSeen is null)
        {
            if (!returningUser) return none;
            var installed = releases.FirstOrDefault(r => Compare(r.Version, current) <= 0);
            return installed is null ? none : new WhatsNewDecision(WhatsNewShow.Window, [installed], null);
        }
        if (Compare(current, lastSeen) <= 0) return none;
        var since = releases.Where(r => Compare(r.Version, lastSeen) > 0 && Compare(r.Version, current) <= 0).ToList();
        if (since.Count == 0) return none;
        var (now, before) = (Split(current).Core!, Split(lastSeen).Core);
        var bigger = before is null || now.Major != before.Major || now.Minor != before.Minor;
        if (bigger || mode == "all") return new WhatsNewDecision(WhatsNewShow.Window, since, null);
        return new WhatsNewDecision(WhatsNewShow.Notice, since, Summarise(since));
    }

    /// <summary>"3 fixes", "2 improvements and a fix": what a patch update brought, for its notice.</summary>
    private static string Summarise(IReadOnlyList<Release> releases)
    {
        var counts = releases.SelectMany(r => r.Sections).GroupBy(s => s.Title.ToLowerInvariant())
            .Select(g => (Title: g.Key, Count: g.Sum(s => s.Entries.Count))).ToList();
        string Word(string title, int n) => title switch
        {
            "fixes" => n == 1 ? "a fix" : $"{n} fixes",
            "improvements" => n == 1 ? "an improvement" : $"{n} improvements",
            "highlights" => n == 1 ? "a new feature" : $"{n} new features",
            _ => n == 1 ? "a change" : $"{n} changes",
        };
        var parts = counts.Select(c => Word(c.Title, c.Count)).ToList();
        return parts.Count switch
        {
            0 => "small changes",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };
    }
}
