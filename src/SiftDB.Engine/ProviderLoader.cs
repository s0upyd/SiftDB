using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>How a provider fared at startup (design section 14, F18).</summary>
public enum ProviderStatus { Loaded, Incompatible, Failed, Duplicate }

/// <summary>
/// One provider the app found, for Settings › Providers and for saved connections whose provider is missing. What the
/// manifest says is kept even when loading failed, so the reason can name it.
/// </summary>
/// <param name="Folder">Where it was found; null for a provider handed to the engine directly (tests).</param>
/// <param name="Reason">Why it isn't loaded; null when it is.</param>
public sealed record ProviderReport(
    string? Folder, string? Id, string? Name, string? Version, int? ContractMin, int? ContractBuilt, ProviderStatus Status, string? Reason);

/// <summary>The providers that loaded, and a report for every one found (loaded or not).</summary>
public sealed record ProviderLoadResult(IReadOnlyList<IDatabaseProvider> Providers, IReadOnlyList<ProviderReport> Reports);

/// <summary>
/// Loads the providers in <c>providers\&lt;name&gt;\</c> (design section 14, F18). Each folder has a manifest,
/// <c>siftdb-provider.json</c>, read before any of the provider's code runs, so an incompatible provider is reported
/// without being loaded. Each provider gets an <see cref="AssemblyLoadContext"/> of its own, so its dependencies (Npgsql,
/// libpg_query, the AWS SDK) stay out of the app and of each other. A provider that doesn't fit is left out with a
/// reason, never a crash.
/// </summary>
public static class ProviderLoader
{
    public const string ManifestName = "siftdb-provider.json";

    private sealed record Manifest(string? Id, string? Name, string? Version, string? Entry, ContractRange? Contract);
    private sealed record ContractRange(int Min, int Built);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The app's own providers folder, next to the exe.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "providers");

    /// <summary>Every provider folder under <paramref name="directory"/>, in name order. A missing folder means no providers.</summary>
    public static ProviderLoadResult LoadAll(string directory)
    {
        var providers = new List<IDatabaseProvider>();
        var reports = new List<ProviderReport>();
        if (!Directory.Exists(directory)) return new ProviderLoadResult(providers, reports);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.GetDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var (provider, report) = LoadOne(folder, ids);
            reports.Add(report);
            if (provider is not null) { providers.Add(provider); ids.Add(provider.Id); }
        }
        return new ProviderLoadResult(providers, reports);
    }

    private static (IDatabaseProvider?, ProviderReport) LoadOne(string folder, HashSet<string> loadedIds)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path)) return (null, Failed(folder, null, $"There is no {ManifestName} in {folder}."));
        Manifest? m;
        try { m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, Failed(folder, null, $"Its {ManifestName} can't be read: {ex.Message}"));
        }
        if (m is null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Entry) || m.Contract is null)
            return (null, Failed(folder, m, $"Its {ManifestName} needs an id, an entry and a contract range."));

        if (!ProviderContract.Accepts(m.Contract.Min, m.Contract.Built))
        {
            var reason = m.Contract.Min > ProviderContract.CurrentVersion
                ? $"Incompatible: needs contract v{m.Contract.Min} or later; this version of SiftDB has v{ProviderContract.CurrentVersion}. An update of SiftDB may help."
                : $"Incompatible: built for contract v{m.Contract.Built}; this version of SiftDB needs v{ProviderContract.MinimumVersion} or later. An update of the provider may help.";
            return (null, Report(folder, m, ProviderStatus.Incompatible, reason));
        }
        if (loadedIds.Contains(m.Id))
            return (null, Report(folder, m, ProviderStatus.Duplicate, $"Another provider with the id '{m.Id}' is already loaded."));

        var entry = Path.GetFullPath(Path.Combine(folder, m.Entry));
        if (!File.Exists(entry)) return (null, Failed(folder, m, $"Its entry, {m.Entry}, isn't in {folder}."));
        try
        {
            var context = new ProviderLoadContext(entry, m.Id);
            var assembly = context.LoadFromAssemblyPath(entry);
            var types = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDatabaseProvider).IsAssignableFrom(t)).ToList();
            if (types.Count != 1)
                return (null, Failed(folder, m, types.Count == 0
                    ? $"{m.Entry} has no public provider class. (If it has one, it may be built against another copy of SiftDB.Abstractions.)"
                    : $"{m.Entry} has {types.Count} provider classes; it must have exactly one."));
            if (types[0].GetConstructor(Type.EmptyTypes) is null)
                return (null, Failed(folder, m, $"{types[0].Name} needs a constructor without parameters."));
            var provider = (IDatabaseProvider)Activator.CreateInstance(types[0])!;
            if (!string.Equals(provider.Id, m.Id, StringComparison.Ordinal))
                return (null, Failed(folder, m, $"The provider calls itself '{provider.Id}', but its manifest says '{m.Id}'."));
            if (provider.ContractVersion != m.Contract.Built)
                return (null, Failed(folder, m, $"The provider says it was built for contract v{provider.ContractVersion}, but its manifest says v{m.Contract.Built}."));
            return (provider, Report(folder, m, ProviderStatus.Loaded, null));
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            return (null, Failed(folder, m, $"It failed to load: {inner.GetType().Name}: {inner.Message}"));
        }
    }

    private static ProviderReport Report(string folder, Manifest m, ProviderStatus status, string? reason) =>
        new(folder, m.Id, m.Name, m.Version, m.Contract?.Min, m.Contract?.Built, status, reason);

    private static ProviderReport Failed(string folder, Manifest? m, string reason) =>
        new(folder, m?.Id, m?.Name, m?.Version, m?.Contract?.Min, m?.Contract?.Built, ProviderStatus.Failed, reason);

    /// <summary>
    /// A provider's own load context: its dependencies resolve from its folder (native libraries too, through its
    /// .deps.json), while SiftDB.Abstractions and the framework always come from the app. Otherwise IDatabaseProvider
    /// would exist twice and the provider would not be one.
    /// </summary>
    private sealed class ProviderLoadContext(string entryPath, string id) : AssemblyLoadContext($"SiftDB provider {id}")
    {
        private static readonly string Shared = typeof(IDatabaseProvider).Assembly.GetName().Name!;
        private readonly AssemblyDependencyResolver _resolver = new(entryPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (string.Equals(name.Name, Shared, StringComparison.OrdinalIgnoreCase)) return null;
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
