using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// The core's view of a session's transaction (design section 7): the state the server reported, plus what the bar
/// summarises ("3 statements · 42 rows changed"). It never decides the state itself; it only records what the provider's
/// monitor observed, and counts what happened while a transaction was open.
/// </summary>
public sealed class TransactionTracker
{
    public TransactionState State { get; private set; } = TransactionState.None;
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Statements run inside the transaction. The one that opened it (a typed BEGIN) does not count.</summary>
    public int Statements { get; private set; }

    public long RowsChanged { get; private set; }

    /// <summary>The app started this transaction itself (auto-transaction), not the user. Cleared when it ends.</summary>
    public bool Automatic { get; private set; }

    /// <summary>
    /// Auto-transaction began a transaction (the session connected, or the last one ended). Every statement from here on
    /// counts as one in it.
    /// </summary>
    public void StartedAutomatically(TransactionSnapshot observed, DateTimeOffset now)
    {
        Apply(observed, now);
        Automatic = observed.State != TransactionState.None;
    }

    /// <summary>A statement finished and the monitor was asked afterwards.</summary>
    /// <param name="rowsAffected">The statement's reported row count, or a negative number when it has none (a SELECT).</param>
    public void StatementCompleted(TransactionSnapshot observed, long rowsAffected, DateTimeOffset now)
    {
        var wasOpen = State != TransactionState.None;
        Apply(observed, now);
        if (observed.State == TransactionState.None || !wasOpen) return;
        Statements++;
        if (rowsAffected > 0) RowsChanged += rowsAffected;
    }

    /// <summary>The state was observed without a statement running (Begin, Commit and Rollback from the bar).</summary>
    public void Observed(TransactionSnapshot observed, DateTimeOffset now) => Apply(observed, now);

    private void Apply(TransactionSnapshot observed, DateTimeOffset now)
    {
        if (observed.State == TransactionState.None)
        {
            State = TransactionState.None;
            StartedAt = null;
            Statements = 0;
            RowsChanged = 0;
            Automatic = false;
            return;
        }
        // The server tells us when it began; if it cannot (a failed transaction), keep what we had, or "now".
        StartedAt = observed.StartedAt ?? StartedAt ?? now;
        State = observed.State;
    }
}
