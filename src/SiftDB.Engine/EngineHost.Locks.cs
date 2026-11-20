using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// How often the lock watcher looks (design section 14, F3). <c>WaitBefore</c>: how long a statement waits on the database
/// before its blockers are looked for; then every <c>Every</c> while it still waits. <c>BlockingEvery</c>: how often an
/// idle session in an open transaction is checked for sessions it blocks. <c>Tick</c>: how often the watcher wakes.
/// </summary>
public sealed record LockWatchTiming(TimeSpan Tick, TimeSpan WaitBefore, TimeSpan Every, TimeSpan BlockingEvery, TimeSpan Timeout)
{
    public static readonly LockWatchTiming Default =
        new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
}

/// <summary>What the lock watcher last saw for one session, and told the UI.</summary>
internal sealed class LockWatchState
{
    public int Looking;
    public DateTimeOffset? WaitingSince;
    public DateTimeOffset LastBlockers = DateTimeOffset.MinValue, LastBlocking = DateTimeOffset.MinValue;
    public bool BlockersShown;

    /// <summary>The session's own key, once known: another SiftDB session waiting for it can then name its sheet.</summary>
    public string? OwnKey;
    public int BlockingShown;
}

/// <summary>
/// Who's blocking me (design section 14, F3). One watcher for all sessions wakes a few times a second. A session waiting on
/// the database (a statement sent and not answered, or reading its next rows) for 2 s or more has its blockers looked up
/// every 2 s and sent as "blockers", until it stops waiting ("blockers" with none). An idle session in an open transaction
/// is checked every 10 s for sessions waiting on its locks ("blocking"). Each look runs on the provider's side connection,
/// never on the session, and a failed look is simply tried again: it must never disturb the session or the statement.
/// A session whose statement waits only for the UI to take rows is not waiting on the database, so it is not looked at.
/// </summary>
public sealed partial class EngineHost
{
    /// <summary>The watcher's timings; tests shorten them.</summary>
    public LockWatchTiming LockTiming { get; init; } = LockWatchTiming.Default;

    private readonly object _lockWatchGate = new();
    private CancellationTokenSource? _lockWatch;

    private void EnsureLockWatch()
    {
        lock (_lockWatchGate)
        {
            if (_lockWatch is not null) return;
            _lockWatch = new CancellationTokenSource();
            var ct = _lockWatch.Token;
            _ = Task.Run(() => WatchLocksAsync(ct));
        }
    }

    private void StopLockWatch()
    {
        lock (_lockWatchGate) _lockWatch?.Cancel();
    }

    private async Task WatchLocksAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(LockTiming.Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                // Each session on its own, so a slow server never holds up the others' looks.
                foreach (var state in _connections.Values)
                    if (state.Connection.Locks is { } locks) _ = LookAsync(state, locks, ct);
        }
        catch (OperationCanceledException) { /* the engine is closing */ }
    }

    /// <summary>The session is waiting on the database: a statement not answered yet, or one reading its next rows.</summary>
    private static bool WaitingOnDatabase(ConnectionState s) => s.Starting is not null || s.Active is { ReadingFromServer: true };

    private async Task LookAsync(ConnectionState s, ILockInspector locks, CancellationToken ct)
    {
        var w = s.LockWatch;
        if (Interlocked.Exchange(ref w.Looking, 1) == 1) return; // the last look is still running
        try
        {
            // Learnt once, before anything else, so the other sessions can tell this one apart from a stranger.
            w.OwnKey ??= await WithTimeout(locks.SessionKeyAsync, ct);
            var now = DateTimeOffset.UtcNow;
            if (WaitingOnDatabase(s))
            {
                w.WaitingSince ??= now;
                if (now - w.WaitingSince >= LockTiming.WaitBefore && now - w.LastBlockers >= LockTiming.Every)
                {
                    w.LastBlockers = now;
                    var blockers = await WithTimeout(locks.BlockersAsync, ct);
                    // It may have stopped waiting while we looked: then what we found is already out of date.
                    if (!WaitingOnDatabase(s) || !_connections.ContainsKey(s.Id)) return;
                    if (blockers.Count > 0 || w.BlockersShown)
                    {
                        w.BlockersShown = blockers.Count > 0;
                        Event(new { type = "blockers", connectionId = s.Id, blockers, mine = Mine(s, blockers) });
                    }
                }
            }
            else
            {
                w.WaitingSince = null;
                if (w.BlockersShown)
                {
                    w.BlockersShown = false;
                    Event(new { type = "blockers", connectionId = s.Id, blockers = Array.Empty<OtherSession>(), mine = Array.Empty<object>() });
                }
            }

            // Sessions waiting on this one's locks: while its transaction is open. Looked at when the session is idle (a
            // statement of its own has the blockers line instead); what was last said stays until the transaction ends.
            if (s.Tracker.State == TransactionState.None)
            {
                if (w.BlockingShown > 0) SendBlocking(s, []);
            }
            else if (s.SessionLock.CurrentCount == 1 && now - w.LastBlocking >= LockTiming.BlockingEvery)
            {
                w.LastBlocking = now;
                var blocked = await WithTimeout(locks.BlockedByMeAsync, ct);
                if (s.Tracker.State != TransactionState.None && _connections.ContainsKey(s.Id) && (blocked.Count > 0 || w.BlockingShown > 0))
                    SendBlocking(s, blocked);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Offline, a timeout, no permission to read pg_stat_activity: say nothing, and try again at the next look.
        }
        finally
        {
            Volatile.Write(ref w.Looking, 0);
        }
    }

    private void SendBlocking(ConnectionState s, IReadOnlyList<OtherSession> blocked)
    {
        s.LockWatch.BlockingShown = blocked.Count;
        Event(new { type = "blocking", connectionId = s.Id, sessions = blocked, mine = Mine(s, blocked) });
    }

    /// <summary>
    /// Which of these sessions are SiftDB's own (another sheet's), matched by their keys: each as its pid and the engine's
    /// connection id, so the UI can name the sheet and offer to go to it.
    /// </summary>
    private object[] Mine(ConnectionState s, IReadOnlyList<OtherSession> sessions) =>
        [.. sessions.Where(o => o.Key is not null)
            .SelectMany(o => _connections.Values.Where(c => c.Id != s.Id && c.LockWatch.OwnKey == o.Key).Take(1)
                .Select(c => new { pid = o.Pid, connectionId = c.Id }))];

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> look, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LockTiming.Timeout);
        return await look(cts.Token);
    }

    /// <summary>
    /// "cancelOther": cancels another session's statement, or ends that session (<paramref name="terminate"/>), from the
    /// blockers line. The server's refusal comes back as the error. The next look happens straight away.
    /// </summary>
    private async Task CancelOtherAsync(int id, int connectionId, int pid, bool terminate)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        if (state.Connection.Locks is not { } locks) { Error(id, "This database cannot do that."); return; }
        await locks.CancelOtherAsync(pid, terminate, CancellationToken.None);
        state.LockWatch.LastBlockers = state.LockWatch.LastBlocking = DateTimeOffset.MinValue;
        Event(new { type = "ok", replyTo = id });
    }
}
