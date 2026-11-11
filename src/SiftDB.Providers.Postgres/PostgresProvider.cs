using System.Data.Common;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

public sealed class PostgresProvider : IDatabaseProvider
{
    public string Id => "postgres";
    public int ContractVersion => ProviderContract.CurrentVersion;

    public async Task<IProviderConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return new PostgresConnection(conn);
    }

    private sealed class PostgresConnection(NpgsqlConnection conn) : IProviderConnection
    {
        public async Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
        {
            var cmd = new NpgsqlCommand(sql, conn);
            return await cmd.ExecuteReaderAsync(ct);
        }

        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }
}
