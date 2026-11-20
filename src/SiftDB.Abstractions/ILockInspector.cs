namespace SiftDB.Abstractions;

/// <summary>
/// Another database session, as "Who's blocking me" shows it (design section 14, F3). Times are in seconds, null when the
/// server does not say. <paramref name="Running"/>: it is running a statement now (cancelling its query can free the
/// lock); when false it is idle inside a transaction, and only ending its session releases what it holds.
/// <paramref name="CanSignal"/>: this session's role may cancel it or end it. <paramref name="Key"/>: who the session is,
/// unique across servers (a pid alone is not: two servers can both have a pid 4711), so it can be matched to a session
/// SiftDB itself has open (<see cref="ILockInspector.SessionKeyAsync"/>); null when the server does not say.
/// </summary>
public sealed record OtherSession(
    string? Key,
    int Pid,
    string? User,
    string? Application,
    string? Client,
    string? State,
    bool Running,
    double? InTransactionSeconds,
    double? RunningSeconds,
    bool WaitingForLock,
    string? Query,
    bool CanSignal);

/// <summary>
/// Locks between this session and others (design section 14, F3), read on a side connection, never on the session itself
/// (which is busy waiting). A connection that offers it returns it from <see cref="IProviderConnection.Locks"/>.
/// </summary>
public interface ILockInspector
{
    /// <summary>
    /// This session's own <see cref="OtherSession.Key"/>, so that when another of SiftDB's sessions waits for it, it can be
    /// named (a sheet's tab) rather than shown as a stranger. Null when it cannot be told.
    /// </summary>
    Task<string?> SessionKeyAsync(CancellationToken ct);

    /// <summary>The sessions this session is waiting for, the longest-running transaction first. Empty when it waits for none.</summary>
    Task<IReadOnlyList<OtherSession>> BlockersAsync(CancellationToken ct);

    /// <summary>The sessions waiting for a lock this session holds (its open transaction).</summary>
    Task<IReadOnlyList<OtherSession>> BlockedByMeAsync(CancellationToken ct);

    /// <summary>
    /// Cancels another session's running statement, or (<paramref name="terminate"/>) ends that session, which rolls back
    /// its transaction. Throws with the server's message when it is refused (not allowed, or the session is gone).
    /// </summary>
    Task CancelOtherAsync(int pid, bool terminate, CancellationToken ct);
}
