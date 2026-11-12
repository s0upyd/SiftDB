using System.Runtime.Versioning;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

internal sealed class MemorySecrets : ISecretStore
{
    public readonly Dictionary<string, string> Values = new();
    public string? Get(string id) => Values.GetValueOrDefault(id);
    public void Set(string id, string secret) => Values[id] = secret;
    public void Remove(string id) => Values.Remove(id);
}

/// <summary>Saved connections on disk (design section 8): no secrets in the file, unique names, atomic and careful writes.</summary>
public sealed class ConnectionStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-store-");
    private readonly MemorySecrets _secrets = new();
    private string FilePath => Path.Combine(_dir.FullName, "connections.json");

    public void Dispose() => _dir.Delete(recursive: true);

    private ConnectionStore NewStore() => new(FilePath, _secrets);

    private static SavedConnection Conn(string name, string id = "", bool remember = true, bool readOnly = false) =>
        new(id, name, "postgres", new Dictionary<string, string> { ["host"] = "h", ["database"] = "d" }, remember, readOnly);

    [Fact]
    public void An_empty_store_lists_nothing_and_writes_nothing()
    {
        Assert.Empty(NewStore().List());
        Assert.Null(NewStore().LastUsed);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Saved_connections_survive_a_restart_and_the_password_stays_out_of_the_file()
    {
        var saved = NewStore().Save(Conn("dev", readOnly: true), "hunter2");
        Assert.NotEmpty(saved.Id);

        var reloaded = NewStore().List().Single();
        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal("dev", reloaded.Name);
        Assert.True(reloaded.ReadOnly);
        Assert.Equal("h", reloaded.Fields["host"]);
        Assert.Equal("hunter2", _secrets.Get(saved.Id));
        Assert.DoesNotContain("hunter2", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Auto_transaction_is_saved_and_off_unless_asked_for()
    {
        var store = NewStore();
        var off = store.Save(Conn("off"), null);
        var on = store.Save(Conn("on") with { AutoTransaction = true }, null);
        Assert.False(NewStore().Get(off.Id)!.AutoTransaction);
        Assert.True(NewStore().Get(on.Id)!.AutoTransaction);
    }

    [Fact]
    public void Names_are_unique_ignoring_case_but_renaming_itself_is_fine()
    {
        var store = NewStore();
        var dev = store.Save(Conn("dev"), null);
        var ex = Assert.Throws<ArgumentException>(() => store.Save(Conn("DEV"), null));
        Assert.Contains("already a connection", ex.Message);
        store.Save(dev with { Name = "Dev" }, null); // same connection, new casing
        Assert.Equal("Dev", store.List().Single().Name);
    }

    [Fact]
    public void A_name_is_required() =>
        Assert.Throws<ArgumentException>(() => NewStore().Save(Conn("  "), null));

    [Fact]
    public void A_null_password_keeps_the_saved_one_an_empty_one_clears_it()
    {
        var store = NewStore();
        var c = store.Save(Conn("dev"), "one");
        store.Save(c with { Fields = new Dictionary<string, string> { ["host"] = "other" } }, null);
        Assert.Equal("one", _secrets.Get(c.Id));
        store.Save(c, "");
        Assert.Null(_secrets.Get(c.Id));
    }

    [Fact]
    public void Not_remembering_the_password_forgets_the_saved_one()
    {
        var store = NewStore();
        var c = store.Save(Conn("dev"), "one");
        store.Save(c with { RememberPassword = false }, "typed but not to be kept");
        Assert.Null(_secrets.Get(c.Id));
    }

    [Fact]
    public void Deleting_removes_the_connection_its_password_and_last_used()
    {
        var store = NewStore();
        var c = store.Save(Conn("dev"), "pw");
        store.SetLastUsed(c.Id);
        store.Delete(c.Id);
        Assert.Empty(NewStore().List());
        Assert.Null(NewStore().LastUsed);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public void Last_used_is_remembered_and_ignores_unknown_ids()
    {
        var store = NewStore();
        var c = store.Save(Conn("dev"), null);
        store.SetLastUsed("nope");
        Assert.Null(NewStore().LastUsed);
        store.SetLastUsed(c.Id);
        Assert.Equal(c.Id, NewStore().LastUsed);
    }

    [Fact]
    public void Empty_field_values_are_not_stored()
    {
        var c = NewStore().Save(Conn("dev") with { Fields = new Dictionary<string, string> { ["host"] = " h ", ["port"] = "" } }, null);
        Assert.Equal(new Dictionary<string, string> { ["host"] = "h" }, NewStore().Get(c.Id)!.Fields);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_refused_and_left_alone()
    {
        const string newer = """{"version":2,"connections":[]}""";
        File.WriteAllText(FilePath, newer);
        var store = NewStore();
        var ex = Assert.Throws<InvalidDataException>(() => store.List());
        Assert.Contains("newer SiftDB", ex.Message);
        Assert.Throws<InvalidDataException>(() => store.Save(Conn("dev"), null));
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_broken_file_is_reported_and_never_overwritten()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = NewStore();
        Assert.Throws<InvalidDataException>(() => store.Save(Conn("dev"), null));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Settings_a_later_version_added_survive_a_rewrite()
    {
        File.WriteAllText(FilePath, """
            {"version":1,"connections":[{"id":"a1","name":"dev","provider":"postgres","fields":{"host":"h"},"colour":"red"}]}
            """);
        var store = NewStore();
        store.Save(store.Get("a1")! with { Name = "dev2" }, null);
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        var c = doc.RootElement.GetProperty("connections")[0];
        Assert.Equal("dev2", c.GetProperty("name").GetString());
        Assert.Equal("red", c.GetProperty("colour").GetString());
    }

    [Fact]
    public void Writes_leave_no_temp_file_behind()
    {
        NewStore().Save(Conn("dev"), null);
        Assert.Equal(["connections.json"], _dir.GetFiles().Select(f => f.Name));
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public void Dpapi_secrets_round_trip_and_are_not_stored_in_clear()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir.FullName, "secrets.json");
        var secrets = new DpapiSecretStore(path);
        secrets.Set("a", "pässwörd;=\"");
        Assert.Equal("pässwörd;=\"", new DpapiSecretStore(path).Get("a"));
        Assert.DoesNotContain("pässwörd", File.ReadAllText(path));
        secrets.Remove("a");
        Assert.Null(new DpapiSecretStore(path).Get("a"));
        Assert.Null(secrets.Get("never set"));
    }
}
