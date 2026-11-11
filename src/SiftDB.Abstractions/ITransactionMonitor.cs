namespace SiftDB.Abstractions;

/// <summary>Where a session stands (design section 7).</summary>
public enum TransactionState
{
    /// <summary>No transaction: each statement commits by itself.</summary>
    None,
    /// <summary>A transaction is open and nothing has changed in it yet.</summary>
    Open,
    /// <summary>The transaction has written something that is not committed.</summary>
    Changed,
    /// <summary>An error aborted the transaction; only a rollback gets out of it (Postgres).</summary>
    Failed,
}

/// <param name="StartedAt">When the open transaction began, if the server can say; otherwise null.</param>
public readonly record struct TransactionSnapshot(TransactionState State, DateTimeOffset? StartedAt);

/// <summary>
/// Control and observation of a session's transaction. The state always comes from the server, never from the app's own
/// bookkeeping, so the Begin button and a typed <c>BEGIN</c> can never disagree.
/// </summary>
public interface ITransactionMonitor
{
    /// <summary>Whether <see cref="TransactionState.Changed"/> can be told apart from <see cref="TransactionState.Open"/>.
    /// When it cannot, the engine treats anything but a plain SELECT as a change (better yellow wrongly than green).</summary>
    bool CanDetectWrites { get; }

    Task BeginAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);

    /// <summary>Asks the server. The session must be idle: never call this while a query is streaming on it.</summary>
    Task<TransactionSnapshot> GetStateAsync(CancellationToken ct);
}
