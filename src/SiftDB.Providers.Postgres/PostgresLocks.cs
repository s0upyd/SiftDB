using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Who's blocking me (design section 14, F3), from <c>pg_stat_activity</c> and <c>pg_blocking_pids</c>, always on a
/// pooled side connection: the session itself is busy waiting, or must not be disturbed. <paramref name="pid"/> is the
/// session's backend process id.
/// </summary>
internal sealed class PostgresLocks(string connectionString, Func<int> pid) : ILockInspector
{
    // can_signal follows the server's own rule for pg_cancel_backend and pg_terminate_backend: a superuser may signal
    // anyone; anyone may signal a session of a role whose privileges they have; members of pg_signal_backend may signal
    // any session that is not a superuser's.
    private const string Columns = $"""
        select {KeyOf}, a.pid, a.usename, a.application_name, host(a.client_addr), a.state,
               extract(epoch from now() - a.xact_start)::float8, extract(epoch from now() - a.query_start)::float8,
               a.wait_event_type = 'Lock', left(a.query, 4000),
               coalesce(
                 (select rolsuper from pg_roles where rolname = current_user)
                 or pg_has_role(current_user, a.usesysid, 'USAGE')
                 or (pg_has_role(current_user, 'pg_signal_backend', 'MEMBER')
                     and not coalesce((select rolsuper from pg_roles where oid = a.usesysid), false)),
                 false)
        from pg_stat_activity a
        """;

    // A session is its pid and the moment it connected, to the microsecond: pids are reused and repeat across servers,
    // the pair does not.
    private const string KeyOf = "a.pid::text || ':' || (extract(epoch from a.backend_start) * 1000000)::bigint::text";

    public async Task<string?> SessionKeyAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"select {KeyOf} from pg_stat_activity a where a.pid = @pid", conn);
        cmd.Parameters.AddWithValue("pid", pid());
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public Task<IReadOnlyList<OtherSession>> BlockersAsync(CancellationToken ct) =>
        ReadAsync($"{Columns} where a.pid = any(pg_blocking_pids(@pid)) order by a.xact_start nulls last", ct);

    // Only sessions waiting for a lock can be blocked, so pg_blocking_pids (which takes the lock manager's locks) is asked
    // about those alone.
    public Task<IReadOnlyList<OtherSession>> BlockedByMeAsync(CancellationToken ct) =>
        ReadAsync($"{Columns} where a.wait_event_type = 'Lock' and a.pid <> @pid and @pid = any(pg_blocking_pids(a.pid)) order by a.query_start nulls last", ct);

    public async Task CancelOtherAsync(int target, bool terminate, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(terminate ? "select pg_terminate_backend(@target)" : "select pg_cancel_backend(@target)", conn);
        cmd.Parameters.AddWithValue("target", target);
        // Not allowed raises an error, which goes to the caller with the server's message. False means no such session.
        if (await cmd.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException($"Session {target} is no longer there.");
    }

    private async Task<IReadOnlyList<OtherSession>> ReadAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pid", pid());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OtherSession>();
        while (await r.ReadAsync(ct))
        {
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            double? Seconds(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            var state = Text(5);
            var query = Text(9);
            list.Add(new OtherSession(
                Text(0), r.GetInt32(1), Text(2), NullIfEmpty(Text(3)), Text(4), state, Running: state == "active",
                Seconds(6), Seconds(7), !r.IsDBNull(8) && r.GetBoolean(8), query is not null && IsSiftDbHousekeeping(query) ? null : query,
                !r.IsDBNull(10) && r.GetBoolean(10)));
        }
        return list;
    }

    private static readonly System.Text.RegularExpressions.Regex Housekeeping = new(
        @"^\s*(select (pg_current_xact_id_if_assigned|txid_current_if_assigned)\(\) is not null, transaction_timestamp\(\)|(release |rollback to )?savepoint siftdb_\w+)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A SiftDB sheet's own bookkeeping, sent after the user's statements (the transaction-state check, its savepoints,
    /// several joined by ";"). Shown as a blocker's last statement it would mislead, so it is left out (null): the user's
    /// own statement is not known any more.
    /// </summary>
    internal static bool IsSiftDbHousekeeping(string query) =>
        query.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(Housekeeping.IsMatch);

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
