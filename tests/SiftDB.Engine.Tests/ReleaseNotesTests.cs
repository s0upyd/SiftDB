using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>Release notes and What's New (design section 14, F17): the changelog's layout, version order, and what shows when.</summary>
public class ReleaseNotesTests
{
    // As git-cliff writes it with cliff.toml.
    private const string Changelog = """
        # Changelog

        What changed in each SiftDB release. Generated from the commit messages by git-cliff (see cliff.toml); do not edit it by hand.

        ## 0.3.1 - 2026-12-10

        ### Fixes

        - Pasting a list no longer drops the last value
        - Format SQL keeps a comment after a comma

        ## 0.3.0 - 2026-12-01

        ### Breaking changes

        - Sheet files use a new header format

        ### Highlights

        - Dry run for UPDATE and DELETE ([Try it](command:dryRun))
        - Format SQL ([Try it](command:format))

        ### Improvements

        - Faster schema load on large databases

        ## 0.3.0-beta.1 - 2026-11-25

        ### Highlights

        - Dry run, early

        ## 0.2.0 - 2026-11-18

        ### Other changes

        - Missing WHERE alarm: ask before an UPDATE or DELETE with no WHERE, or a TRUNCATE

        """;

    private static readonly IReadOnlyList<Release> Releases = ReleaseNotes.Parse(Changelog);

    [Fact]
    public void Reads_the_changelog_newest_first()
    {
        Assert.Equal(["0.3.1", "0.3.0", "0.3.0-beta.1", "0.2.0"], Releases.Select(r => r.Version));
        var r = Releases[1];
        Assert.Equal("2026-12-01", r.Date);
        Assert.Equal(["Breaking changes", "Highlights", "Improvements"], r.Sections.Select(s => s.Title));
        Assert.Equal(new ReleaseEntry("Dry run for UPDATE and DELETE", "dryRun"), r.Sections[1].Entries[0]); // the link is taken off
        Assert.Null(r.Sections[2].Entries[0].TryIt);
    }

    [Fact]
    public void Unreleased_and_empty_releases_are_left_out() =>
        Assert.Equal(["1.0.0"], ReleaseNotes.Parse("## Unreleased\n\n### Fixes\n\n- x\n\n## 1.0.1 - 2027-01-01\n\n## 1.0.0 - 2026-12-31\n\n### Fixes\n\n- y\n").Select(r => r.Version));

    [Theory]
    [InlineData("0.10.0", "0.9.1", 1)]
    [InlineData("0.2.0", "0.2.0-beta.1", 1)]
    [InlineData("0.2.0-beta.2", "0.2.0-beta.10", -1)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("rubbish", "0.0.1", -1)]
    public void Versions_are_ordered_as_semantic_versions(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ReleaseNotes.Compare(a, b)));

    [Fact]
    public void A_fresh_install_shows_nothing() =>
        Assert.Equal(WhatsNewShow.None, ReleaseNotes.Decide("0.3.1", null, "majorMinor", Releases).Show);

    [Fact]
    public void A_minor_update_opens_whats_new_with_every_release_since()
    {
        var d = ReleaseNotes.Decide("0.3.1", "0.2.0", "majorMinor", Releases);
        Assert.Equal(WhatsNewShow.Window, d.Show);
        Assert.Equal(["0.3.1", "0.3.0", "0.3.0-beta.1"], d.Releases.Select(r => r.Version)); // skipped releases too, newest first
    }

    [Fact]
    public void A_patch_update_is_a_notice_saying_what_it_brought()
    {
        var d = ReleaseNotes.Decide("0.3.1", "0.3.0", "majorMinor", Releases);
        Assert.Equal(WhatsNewShow.Notice, d.Show);
        Assert.Equal("2 fixes", d.Summary);
        Assert.Equal(WhatsNewShow.Window, ReleaseNotes.Decide("0.3.1", "0.3.0", "all", Releases).Show);
    }

    [Theory]
    [InlineData("0.3.1", "0.2.0", "never")]   // switched off
    [InlineData("0.3.0", "0.3.1", "all")]     // an older build than the one last seen
    [InlineData("0.3.1", "0.3.1", "all")]     // nothing new
    [InlineData("0.4.0", "0.3.1", "all")]     // no notes for it (a local build)
    public void Otherwise_nothing(string current, string lastSeen, string mode) =>
        Assert.Equal(WhatsNewShow.None, ReleaseNotes.Decide(current, lastSeen, mode, Releases).Show);

    [Fact]
    public async Task The_command_records_the_version_so_it_never_shows_twice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"siftdb-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var events = new List<string>();
            await using var engine = new EngineHost([], new Output(events), settings: store);
            await engine.HandleCommandAsync("""{"type":"whatsNew","id":1,"version":"0.3.1"}""");
            var reply = JsonDocument.Parse(events.Last()).RootElement;
            Assert.Equal("none", reply.GetProperty("show").GetString());     // a fresh install
            Assert.Equal("0.3.1", store.Get().LastSeenVersion);

            await engine.HandleCommandAsync("""{"type":"whatsNew","id":2,"version":"0.3.0"}""");
            Assert.Equal("0.3.1", store.Get().LastSeenVersion);                 // an older build never moves it back
        }
        finally { File.Delete(path); }
    }

    /// <summary>A build with CHANGELOG.md (a release build) ships notes the engine can read; one without has none.</summary>
    [Fact]
    public void The_shipped_changelog_is_read_when_there_is_one()
    {
        var shipped = typeof(ReleaseNotes).Assembly.GetManifestResourceNames().Contains("SiftDB.CHANGELOG.md");
        if (shipped) Assert.NotEmpty(ReleaseNotes.All);
        else Assert.Empty(ReleaseNotes.All); // only release builds have CHANGELOG.md
    }

    private sealed class Output(List<string> events) : IEngineOutput
    {
        public void SendEvent(string json) => events.Add(json);
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }
}
