using System.Runtime.Loader;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// The Postgres provider loaded the way the app loads it (design section 14, F18): from providers\postgres\ in the build
/// output, into a load context of its own, native libpg_query included. Runs on Linux in CI as well as on Windows.
/// </summary>
public class ProviderLoadingTests
{
    // SIFTDB_PROVIDERS_DIR points these at another copy, such as a release publish folder, to check that it loads too.
    private static readonly string Providers = Environment.GetEnvironmentVariable("SIFTDB_PROVIDERS_DIR") is { Length: > 0 } dir
        ? dir : Path.Combine(AppContext.BaseDirectory, "providers");

    [Fact]
    public void Postgres_loads_from_its_own_folder_into_its_own_context()
    {
        var result = ProviderLoader.LoadAll(Providers);
        var report = Assert.Single(result.Reports);
        Assert.Equal(ProviderStatus.Loaded, report.Status);
        Assert.Equal("postgres", report.Id);
        Assert.Equal("PostgreSQL", report.Name);

        var p = Assert.Single(result.Providers);
        Assert.Equal("postgres", p.Id);
        var context = AssemblyLoadContext.GetLoadContext(p.GetType().Assembly)!;
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.StartsWith(Path.Combine(Providers, "postgres"), p.GetType().Assembly.Location);
        // The contract is shared: the provider's IDatabaseProvider is the app's, so the cast above held.
        Assert.Same(typeof(IDatabaseProvider).Assembly, AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "SiftDB.Abstractions"));
    }

    [Fact]
    public void Its_native_parser_and_its_dependencies_resolve_from_its_folder()
    {
        var p = Assert.Single(ProviderLoader.LoadAll(Providers).Providers);
        // libpg_query parses this; Npgsql builds the string. Both come from providers\postgres\.
        Assert.True(p.CannotRunInTransaction("vacuum"));
        Assert.NotNull(p.CheckReadOnly("set transaction read write"));
        Assert.Contains("Host=db", p.BuildConnectionString(new Dictionary<string, string> { ["host"] = "db", ["database"] = "x" }, null));
        var npgsql = AssemblyLoadContext.GetLoadContext(p.GetType().Assembly)!.Assemblies.Single(a => a.GetName().Name == "Npgsql");
        Assert.StartsWith(Path.Combine(Providers, "postgres"), npgsql.Location);
    }

    [SkippableFact]
    public async Task A_loaded_provider_connects_and_queries()
    {
        var cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
        Skip.If(string.IsNullOrEmpty(cs), "SIFTDB_SPIKE_CONN not set");
        var p = Assert.Single(ProviderLoader.LoadAll(Providers).Providers);
        await using var conn = await p.OpenAsync(cs, ConnectionOptions.Default, CancellationToken.None);
        await using var reader = await conn.ExecuteReaderAsync("select 41 + 1", CancellationToken.None);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt32(0));
    }

    [Fact]
    public void The_manifest_says_the_contract_it_was_built_for()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Providers, "postgres", ProviderLoader.ManifestName)));
        var contract = doc.RootElement.GetProperty("contract");
        // build/SiftDB.Provider.targets holds the number the manifest is written with; it must follow the code.
        Assert.Equal(ProviderContract.CurrentVersion, contract.GetProperty("built").GetInt32());
        Assert.True(ProviderContract.Accepts(contract.GetProperty("min").GetInt32(), contract.GetProperty("built").GetInt32()));
        Assert.Equal("SiftDB.Providers.Postgres.dll", doc.RootElement.GetProperty("entry").GetString());
    }

    [Fact]
    public void The_provider_folder_has_no_copy_of_the_contract()
    {
        Assert.False(File.Exists(Path.Combine(Providers, "postgres", "SiftDB.Abstractions.dll")));
    }

    [Fact]
    public void A_second_provider_with_the_same_id_is_left_out()
    {
        var dir = Directory.CreateTempSubdirectory("siftdb-providers-");
        try
        {
            foreach (var name in new[] { "a-postgres", "b-postgres" }) Copy(Path.Combine(Providers, "postgres"), Path.Combine(dir.FullName, name));
            var result = ProviderLoader.LoadAll(dir.FullName);
            Assert.Single(result.Providers);
            Assert.Equal([ProviderStatus.Loaded, ProviderStatus.Duplicate], result.Reports.Select(r => r.Status));
            Assert.Contains("'postgres'", result.Reports[1].Reason);
        }
        finally { try { dir.Delete(true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } } // loaded assemblies stay locked on Windows
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
