using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace SiftDB.Host;

/// <summary>
/// How a check went. <c>NotInstalled</c>: run from a build folder, which never updates. <c>Ready</c>: <c>Version</c> is
/// downloaded and waits for a restart (found now or earlier). <c>Failed</c>: <c>Error</c> says why (often: offline).
/// </summary>
public enum UpdateStatus { NotInstalled, UpToDate, Ready, Failed }

public sealed record UpdateCheck(UpdateStatus Status, string? Version, string? Error);

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
    /// Checks for a newer release and downloads it. <paramref name="wait"/>: a check asked for by hand waits for one already
    /// running (the timer's) and then checks again; the timer's own never waits. A failure is normal (offline), and the next
    /// check tries again; asked for by hand, it is said. <paramref name="progress"/> gets the download's percent.
    /// </summary>
    public async Task<UpdateCheck> CheckAndDownloadAsync(bool wait = false, Action<string, int>? progress = null)
    {
        if (!_manager.IsInstalled) return new UpdateCheck(UpdateStatus.NotInstalled, null, null);
        if (wait) await _checking.WaitAsync();
        else if (!await _checking.WaitAsync(0)) return Result();
        try
        {
            _ready ??= _manager.UpdatePendingRestart;
            var info = await _manager.CheckForUpdatesAsync();
            if (info is not null && (_ready is null || info.TargetFullRelease.Version > _ready.Version))
            {
                var version = info.TargetFullRelease.Version.ToString();
                await _manager.DownloadUpdatesAsync(info, p => progress?.Invoke(version, p));
                _ready = info.TargetFullRelease;
            }
            return Result();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Update check failed: {e.Message}");
            return new UpdateCheck(UpdateStatus.Failed, _ready?.Version.ToString(), e.Message);
        }
        finally
        {
            _checking.Release();
        }
    }

    private UpdateCheck Result() =>
        _ready is { } r ? new UpdateCheck(UpdateStatus.Ready, r.Version.ToString(), null) : new UpdateCheck(UpdateStatus.UpToDate, null, null);

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
