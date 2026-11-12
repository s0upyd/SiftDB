using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>Autosave and restore (design section 6).</summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-session-");
    private string SessionDir => Path.Combine(_dir.FullName, "session");
    private SessionStore NewStore() => new(SessionDir);

    public void Dispose() => _dir.Delete(recursive: true);

    private static SessionSheet Sheet(string id, string? path = null, bool dirty = true, string? savedId = null, string title = "Sheet 1") =>
        new(id, title, path, dirty, savedId, null, "assisted", SheetFile.Crlf, "utf-8", []);

    private static Dictionary<string, string> Texts(params (string Id, string Text)[] t) => t.ToDictionary(x => x.Id, x => x.Text);

    [Fact]
    public void Nothing_saved_restores_nothing()
    {
        var (sheets, active) = NewStore().Load();
        Assert.Empty(sheets);
        Assert.Null(active);
    }

    [Fact]
    public void Sheets_come_back_in_order_with_their_text_and_the_active_one()
    {
        NewStore().Save([Sheet("a", title: "Sheet 1", savedId: "c1"), Sheet("b", title: "Sheet 2")], "b", Texts(("a", "select 1"), ("b", "select 2")));
        var (sheets, active) = NewStore().Load();
        Assert.Equal(["a", "b"], sheets.Select(s => s.Sheet.Id));
        Assert.Equal(["select 1", "select 2"], sheets.Select(s => s.Text));
        Assert.Equal("c1", sheets[0].Sheet.SavedId);
        Assert.Equal("b", active);
    }

    [Fact]
    public void Text_not_sent_again_keeps_what_was_written_before()
    {
        var store = NewStore();
        store.Save([Sheet("a"), Sheet("b")], "a", Texts(("a", "one"), ("b", "two")));
        store.Save([Sheet("a"), Sheet("b")], "a", Texts(("a", "one, edited")));
        Assert.Equal(["one, edited", "two"], NewStore().Load().Sheets.Select(s => s.Text));
    }

    [Fact]
    public void Closed_sheets_lose_their_autosaved_text()
    {
        var store = NewStore();
        store.Save([Sheet("a"), Sheet("b")], "a", Texts(("a", "one"), ("b", "two")));
        store.Save([Sheet("a")], "a", Texts());
        Assert.False(File.Exists(Path.Combine(SessionDir, "sheets", "b.sql")));
        Assert.Single(NewStore().Load().Sheets);
    }

    [Fact]
    public void A_saved_file_with_no_edits_comes_back_from_the_file_as_it_is_now()
    {
        var file = Path.Combine(_dir.FullName, "q.sql");
        File.WriteAllText(file, "select 'old';\n");
        NewStore().Save([Sheet("a", path: file, dirty: false)], "a", Texts(("a", "select 'old';\n")));
        File.WriteAllText(file, "-- db: mode=strict\nselect 'changed outside';\n"); // e.g. a git pull while SiftDB was closed

        var restored = NewStore().Load().Sheets.Single();
        Assert.Equal("select 'changed outside';\n", restored.Text);
        Assert.False(restored.Sheet.Dirty);
        Assert.False(restored.Missing);
        Assert.Equal(SheetFile.Lf, restored.Sheet.LineEnding);
    }

    [Fact]
    public void A_file_with_unsaved_edits_comes_back_from_the_autosave()
    {
        var file = Path.Combine(_dir.FullName, "q.sql");
        File.WriteAllText(file, "select 'saved';");
        NewStore().Save([Sheet("a", path: file, dirty: true)], "a", Texts(("a", "select 'edited, not saved';")));
        var restored = NewStore().Load().Sheets.Single();
        Assert.Equal("select 'edited, not saved';", restored.Text);
        Assert.True(restored.Sheet.Dirty);
    }

    [Fact]
    public void A_file_that_has_gone_comes_back_from_the_autosave_as_unsaved()
    {
        var file = Path.Combine(_dir.FullName, "gone.sql");
        File.WriteAllText(file, "select 1;");
        NewStore().Save([Sheet("a", path: file, dirty: false)], "a", Texts(("a", "select 1;")));
        File.Delete(file);
        var restored = NewStore().Load().Sheets.Single();
        Assert.True(restored.Missing);
        Assert.True(restored.Sheet.Dirty);
        Assert.Equal("select 1;", restored.Text);
    }

    [Fact]
    public void A_broken_manifest_restores_nothing_instead_of_failing()
    {
        Directory.CreateDirectory(SessionDir);
        File.WriteAllText(Path.Combine(SessionDir, "session.json"), "{ not json");
        Assert.Empty(NewStore().Load().Sheets);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Sheet_ids_cannot_point_outside_the_session_folder(string id) =>
        Assert.Throws<ArgumentException>(() => NewStore().Save([Sheet(id)], null, Texts((id, "x"))));
}
