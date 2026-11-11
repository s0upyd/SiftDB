using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>The transaction state as the real server reports it. Needs SIFTDB_SPIKE_CONN; uses a temp table only.</summary>
public class TransactionMonitorTests : IAsyncLifetime
{
    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private IProviderConnection? _conn;
    private ITransactionMonitor Tx => _conn!.Transactions;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        _conn = await new PostgresProvider().OpenAsync(_cs, CancellationToken.None);
        await Exec("create temp table tx_probe (x int)");
    }

    public async Task DisposeAsync()
    {
        if (_conn is not null) await _conn.DisposeAsync();
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    /// <summary>Runs a statement to completion, the way the engine does (read the reader to the end, then close it).</summary>
    private async Task Exec(string sql)
    {
        await using var r = await _conn!.ExecuteReaderAsync(sql, CancellationToken.None);
        while (await r.ReadAsync()) { }
    }

    private async Task<TransactionState> State() => (await Tx.GetStateAsync(CancellationToken.None)).State;

    [SkippableFact]
    public async Task A_fresh_session_has_no_transaction()
    {
        Require();
        Assert.Equal(TransactionState.None, await State());
        Assert.True(Tx.CanDetectWrites);
    }

    [SkippableFact]
    public async Task Begin_opens_a_transaction_and_a_write_makes_it_changed()
    {
        Require();
        await Tx.BeginAsync(CancellationToken.None);
        Assert.Equal(TransactionState.Open, await State());

        await Exec("select 1");                      // reading changes nothing
        Assert.Equal(TransactionState.Open, await State());

        await Exec("insert into tx_probe values (1)");
        Assert.Equal(TransactionState.Changed, await State());
    }

    [SkippableFact]
    public async Task Commit_and_rollback_both_end_the_transaction()
    {
        Require();
        await Tx.BeginAsync(CancellationToken.None);
        await Exec("insert into tx_probe values (1)");
        await Tx.CommitAsync(CancellationToken.None);
        Assert.Equal(TransactionState.None, await State());
        Assert.Equal(1, await Count());

        await Tx.BeginAsync(CancellationToken.None);
        await Exec("insert into tx_probe values (2)");
        await Tx.RollbackAsync(CancellationToken.None);
        Assert.Equal(TransactionState.None, await State());
        Assert.Equal(1, await Count());               // the second insert is gone
    }

    [SkippableFact]
    public async Task A_typed_BEGIN_is_seen_exactly_like_the_button()
    {
        Require();
        await Exec("begin");
        Assert.Equal(TransactionState.Open, await State());
        await Exec("insert into tx_probe values (1)");
        Assert.Equal(TransactionState.Changed, await State());
        await Exec("rollback");
        Assert.Equal(TransactionState.None, await State());
    }

    [SkippableFact]
    public async Task An_error_inside_a_transaction_makes_it_failed_until_rollback()
    {
        Require();
        await Tx.BeginAsync(CancellationToken.None);
        await Assert.ThrowsAsync<PostgresException>(() => Exec("select 1/0"));

        Assert.Equal(TransactionState.Failed, await State());
        // Everything is rejected now, which is what "failed" means.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec("select 1"));
        Assert.Equal("25P02", ex.SqlState);

        await Tx.RollbackAsync(CancellationToken.None);
        Assert.Equal(TransactionState.None, await State());
        await Exec("select 1");                        // usable again
    }

    [SkippableFact]
    public async Task An_error_outside_a_transaction_changes_nothing()
    {
        Require();
        await Assert.ThrowsAsync<PostgresException>(() => Exec("select 1/0"));
        Assert.Equal(TransactionState.None, await State());
    }

    [SkippableFact]
    public async Task The_start_time_comes_from_the_server_and_stays_put()
    {
        Require();
        var before = DateTimeOffset.UtcNow;
        await Tx.BeginAsync(CancellationToken.None);
        var first = await Tx.GetStateAsync(CancellationToken.None);
        await Task.Delay(300);
        await Exec("insert into tx_probe values (1)");
        var second = await Tx.GetStateAsync(CancellationToken.None);

        Assert.NotNull(first.StartedAt);
        Assert.Equal(first.StartedAt, second.StartedAt);           // the transaction's start, not the latest statement's
        Assert.InRange(first.StartedAt!.Value, before.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Null((await ProbeAfterRollback()).StartedAt);
    }

    [SkippableFact]
    public async Task The_probe_does_not_change_the_state_it_measures()
    {
        Require();
        await Tx.BeginAsync(CancellationToken.None);
        for (var i = 0; i < 5; i++) Assert.Equal(TransactionState.Open, await State()); // still Open, no xid assigned
    }

    private async Task<TransactionSnapshot> ProbeAfterRollback()
    {
        await Tx.RollbackAsync(CancellationToken.None);
        return await Tx.GetStateAsync(CancellationToken.None);
    }

    private async Task<long> Count()
    {
        await using var r = await _conn!.ExecuteReaderAsync("select count(*) from tx_probe", CancellationToken.None);
        await r.ReadAsync();
        return r.GetInt64(0);
    }
}

/// <summary>Needs no server: it only checks that the Npgsql internals the monitor reads are still there.</summary>
public class TransactionMonitorGuardTests
{
    [Fact]
    public void The_Npgsql_protocol_status_the_transaction_bar_reads_is_still_available()
    {
        Assert.True(PostgresTransactionMonitor.ProtocolStatusAvailable,
            "Npgsql no longer exposes NpgsqlConnection.Connector.TransactionStatus by reflection. The transaction bar needs " +
            "the server's protocol transaction status: find where this Npgsql version keeps it and update PostgresTransactionMonitor.");
    }
}
