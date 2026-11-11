using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

public class TransactionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static TransactionSnapshot Snap(TransactionState s, DateTimeOffset? at = null) => new(s, at);

    [Fact]
    public void It_starts_with_no_transaction()
    {
        var t = new TransactionTracker();
        Assert.Equal(TransactionState.None, t.State);
        Assert.Null(t.StartedAt);
        Assert.Equal(0, t.Statements);
        Assert.Equal(0, t.RowsChanged);
    }

    [Fact]
    public void The_button_opening_a_transaction_records_the_servers_start_time()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0.AddSeconds(5));
        Assert.Equal(TransactionState.Open, t.State);
        Assert.Equal(T0, t.StartedAt);
        Assert.Equal(0, t.Statements);
    }

    [Fact]
    public void A_typed_BEGIN_opens_the_transaction_but_is_not_counted_as_a_statement_in_it()
    {
        var t = new TransactionTracker();
        t.StatementCompleted(Snap(TransactionState.Open, T0), -1, T0);
        Assert.Equal(TransactionState.Open, t.State);
        Assert.Equal(0, t.Statements);
    }

    [Fact]
    public void Statements_and_changed_rows_accumulate_while_open()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0);

        t.StatementCompleted(Snap(TransactionState.Open, T0), -1, T0);        // a select: counted, no rows changed
        t.StatementCompleted(Snap(TransactionState.Changed, T0), 40, T0);     // an update of 40 rows
        t.StatementCompleted(Snap(TransactionState.Changed, T0), 2, T0);

        Assert.Equal(TransactionState.Changed, t.State);
        Assert.Equal(3, t.Statements);
        Assert.Equal(42, t.RowsChanged);
    }

    [Fact]
    public void Rows_affected_of_zero_or_unknown_add_nothing()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0);
        t.StatementCompleted(Snap(TransactionState.Open, T0), 0, T0);
        t.StatementCompleted(Snap(TransactionState.Open, T0), -1, T0);
        Assert.Equal(0, t.RowsChanged);
        Assert.Equal(2, t.Statements);
    }

    [Fact]
    public void Commit_or_rollback_clears_everything()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0);
        t.StatementCompleted(Snap(TransactionState.Changed, T0), 5, T0);

        t.Observed(Snap(TransactionState.None), T0.AddMinutes(1));

        Assert.Equal(TransactionState.None, t.State);
        Assert.Null(t.StartedAt);
        Assert.Equal(0, t.Statements);
        Assert.Equal(0, t.RowsChanged);
    }

    [Fact]
    public void A_typed_COMMIT_ends_it_and_is_not_counted()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0);
        t.StatementCompleted(Snap(TransactionState.Changed, T0), 5, T0);
        t.StatementCompleted(Snap(TransactionState.None), -1, T0);            // the COMMIT statement itself
        Assert.Equal(0, t.Statements);
        Assert.Equal(TransactionState.None, t.State);
    }

    [Fact]
    public void An_error_turns_it_failed_and_keeps_the_start_time_and_the_counts()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0);
        t.StatementCompleted(Snap(TransactionState.Changed, T0), 7, T0);

        // The server cannot report a start time for a failed transaction.
        t.StatementCompleted(Snap(TransactionState.Failed), -1, T0.AddSeconds(30));

        Assert.Equal(TransactionState.Failed, t.State);
        Assert.Equal(T0, t.StartedAt);
        Assert.Equal(7, t.RowsChanged);
    }

    [Fact]
    public void Without_any_start_time_it_falls_back_to_now()
    {
        var t = new TransactionTracker();
        t.StatementCompleted(Snap(TransactionState.Open), -1, T0);
        Assert.Equal(T0, t.StartedAt);
    }

    [Fact]
    public void A_new_transaction_after_a_rollback_starts_from_zero()
    {
        var t = new TransactionTracker();
        t.Observed(Snap(TransactionState.Open, T0), T0);
        t.StatementCompleted(Snap(TransactionState.Changed, T0), 9, T0);
        t.Observed(Snap(TransactionState.None), T0);
        t.Observed(Snap(TransactionState.Open, T0.AddHours(1)), T0.AddHours(1));

        Assert.Equal(0, t.Statements);
        Assert.Equal(0, t.RowsChanged);
        Assert.Equal(T0.AddHours(1), t.StartedAt);
    }
}
