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

    /// <summary>
    /// Called just before a statement (the text of one run) is sent, with the session idle. A database where an error
    /// aborts the whole transaction (Postgres) can guard the statement here, e.g. with a savepoint, so a failure undoes only
    /// that statement (design section 7). Returns whether it set a guard; by default nothing is guarded.
    /// </summary>
    Task<bool> BeforeStatementAsync(string sql, CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Called once a guarded statement is over (its last row read, or it failed or was cancelled), with the session idle.
    /// Returns true when the statement had failed the transaction and was undone, leaving the transaction open as it was
    /// before the statement.
    /// </summary>
    Task<bool> AfterStatementAsync(bool failed, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// What a finished statement (its reader) or a commit (the transaction monitor) has to say beyond rows and time
/// (contract v17): the read capacity a DynamoDB statement used, or that a write was queued for the transaction rather
/// than run. A reader or monitor that implements it is asked once it is done; the result's status line (or the
/// sheet's message, for a commit) shows it.
/// </summary>
public interface IResultNote
{
    /// <summary>Replaces the status line's headline ("3 rows", "Done"): "Queued". Null keeps it.</summary>
    string? NoteHeadline { get; }

    /// <summary>Added after the time: "4.5 read units", "statement 2 of the transaction, runs at Commit". Null adds nothing.</summary>
    string? NoteDetail { get; }
}
