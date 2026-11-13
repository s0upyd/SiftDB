using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace SiftDB.Host;

/// <summary>
/// Background updates from the public repository's GitHub Releases (design section 9). Only an installed copy updates;
/// a copy run from a build folder never checks. An update is downloaded quietly and then offered: restart into it now,
/// or it is applied the next time SiftDB starts.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/s0upyd/SiftDB";

    // Prereleases are included so that a beta install can find beta builds. Channels keep them apart: a stable install
    // reads only the stable channel's feed, which beta releases do not carry.
    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: true));
    private readonly SemaphoreSlim _checking = new(1, 1);
    private VelopackAsset? _ready;

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The running version: the installed release's, or the build's own when run from a build folder.</summary>
    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// Checks for a newer release and downloads it. Returns the version that is ready to restart into, or null when there
    /// is none (or the check failed: being offline is normal, and the next check tries again).
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled) return null;
        if (!await _checking.WaitAsync(0)) return _ready?.Version.ToString();
        try
        {
            _ready ??= _manager.UpdatePendingRestart;
            var info = await _manager.CheckForUpdatesAsync();
            if (info is not null && (_ready is null || info.TargetFullRelease.Version > _ready.Version))
            {
                await _manager.DownloadUpdatesAsync(info);
                _ready = info.TargetFullRelease;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Update check failed: {e.Message}");
        }
        finally
        {
            _checking.Release();
        }
        return _ready?.Version.ToString();
    }

    /// <summary>
    /// Starts the updater, which waits for this process to exit, applies the downloaded release and starts SiftDB again.
    /// Call it only once the window is certainly closing.
    /// </summary>
    public void ApplyAfterExit()
    {
        if (_ready is null) return;
        _manager.WaitExitThenApplyUpdates(_ready, silent: false, restart: true);
    }
}
