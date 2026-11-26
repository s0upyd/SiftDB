using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>
/// Providers that don't fit are left out with a reason, never a crash (design section 14, F18). A real provider loading
/// is tested with Postgres (ProviderLoadingTests); these are the ways a folder can be wrong.
/// </summary>
public sealed class ProviderLoaderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-providers-");

    // Best effort: on Windows an assembly that was loaded stays locked until the process ends.
    public void Dispose() { try { _dir.Delete(true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    private string Folder(string name, string? manifest, params (string File, byte[] Bytes)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, name)).FullName;
        if (manifest is not null) File.WriteAllText(Path.Combine(folder, ProviderLoader.ManifestName), manifest);
        foreach (var (file, bytes) in files) File.WriteAllBytes(Path.Combine(folder, file), bytes);
        return folder;
    }

    private static string Manifest(string id = "fake", int min = ProviderContract.CurrentVersion, int built = ProviderContract.CurrentVersion, string entry = "Fake.dll") =>
        $$"""{ "id": "{{id}}", "name": "Fake DB", "version": "1.2.3", "entry": "{{entry}}", "contract": { "min": {{min}}, "built": {{built}} } }""";

    private ProviderReport Only()
    {
        var result = ProviderLoader.LoadAll(_dir.FullName);
        Assert.Empty(result.Providers);
        return Assert.Single(result.Reports);
    }

    [Fact]
    public void No_folder_means_no_providers() =>
        Assert.Empty(ProviderLoader.LoadAll(Path.Combine(_dir.FullName, "missing")).Reports);

    [Fact]
    public void A_folder_without_a_manifest_is_reported()
    {
        Folder("stray", null);
        var r = Only();
        Assert.Equal(ProviderStatus.Failed, r.Status);
        Assert.Contains(ProviderLoader.ManifestName, r.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "id": "fake" }""")]
    public void A_manifest_that_cannot_be_used_is_reported(string manifest)
    {
        Folder("fake", manifest);
        Assert.Equal(ProviderStatus.Failed, Only().Status);
    }

    [Fact]
    public void A_provider_needing_a_newer_app_is_incompatible_and_says_what_it_needs()
    {
        Folder("fake", Manifest(min: ProviderContract.CurrentVersion + 1, built: ProviderContract.CurrentVersion + 1));
        var r = Only();
        Assert.Equal(ProviderStatus.Incompatible, r.Status);
        Assert.Contains($"needs contract v{ProviderContract.CurrentVersion + 1} or later", r.Reason);
        Assert.Equal("Fake DB", r.Name);
        Assert.Equal("1.2.3", r.Version);
    }

    [Fact]
    public void A_provider_built_before_the_minimum_is_incompatible()
    {
        Folder("fake", Manifest(min: ProviderContract.MinimumVersion - 1, built: ProviderContract.MinimumVersion - 1));
        Assert.Equal(ProviderStatus.Incompatible, Only().Status);
    }

    [Fact]
    public void A_provider_built_for_a_newer_contract_that_still_works_with_this_one_is_tried()
    {
        // Built later, but it says it works from this contract on: the range holds, so loading is attempted (and fails
        // here only because there is no assembly).
        Folder("fake", Manifest(min: ProviderContract.CurrentVersion, built: ProviderContract.CurrentVersion + 2));
        var r = Only();
        Assert.Equal(ProviderStatus.Failed, r.Status);
        Assert.Contains("Fake.dll", r.Reason);
    }

    [Fact]
    public void A_missing_entry_is_reported()
    {
        Folder("fake", Manifest());
        var r = Only();
        Assert.Equal(ProviderStatus.Failed, r.Status);
        Assert.Contains("Fake.dll", r.Reason);
    }

    [Fact]
    public void An_entry_that_is_not_an_assembly_is_reported_not_thrown()
    {
        Folder("fake", Manifest(), ("Fake.dll", "MZ but not really"u8.ToArray()));
        var r = Only();
        Assert.Equal(ProviderStatus.Failed, r.Status);
        Assert.Contains("failed to load", r.Reason);
    }

    [Fact]
    public void An_assembly_without_a_provider_is_reported()
    {
        // Any managed assembly without an IDatabaseProvider in it will do: one of the app's own.
        var bridge = Path.Combine(AppContext.BaseDirectory, "SiftDB.Bridge.dll");
        Folder("fake", Manifest(entry: Path.GetFileName(bridge)), (Path.GetFileName(bridge), File.ReadAllBytes(bridge)));
        var r = Only();
        Assert.Equal(ProviderStatus.Failed, r.Status);
        Assert.Contains("no public provider class", r.Reason);
    }

    [Fact]
    public void Each_folder_is_reported_in_name_order_and_one_bad_one_does_not_stop_the_rest()
    {
        Folder("b", "not json");
        Folder("a", Manifest(min: 999, built: 999));
        var result = ProviderLoader.LoadAll(_dir.FullName);
        Assert.Equal([ProviderStatus.Incompatible, ProviderStatus.Failed], result.Reports.Select(r => r.Status));
    }

    [Theory]
    [InlineData(ProviderContract.CurrentVersion, ProviderContract.CurrentVersion, true)]
    [InlineData(ProviderContract.MinimumVersion, ProviderContract.CurrentVersion + 5, true)]
    [InlineData(ProviderContract.CurrentVersion + 1, ProviderContract.CurrentVersion + 1, false)]
    [InlineData(ProviderContract.MinimumVersion - 1, ProviderContract.MinimumVersion - 1, false)]
    [InlineData(ProviderContract.CurrentVersion, ProviderContract.CurrentVersion - 1, false)] // min above built: nonsense
    public void The_contract_range(int min, int built, bool accepted) => Assert.Equal(accepted, ProviderContract.Accepts(min, built));
}
