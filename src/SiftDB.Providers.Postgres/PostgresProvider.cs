using System.Data.Common;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

public sealed class PostgresProvider : IDatabaseProvider
{
    public string Id => "postgres";
    public int ContractVersion => ProviderContract.CurrentVersion;
    public ISqlParser Parser { get; }
    public IIdentifierPolicy Identifiers { get; }

    public PostgresProvider()
    {
        var parser = new PostgresSqlParser();
        Parser = parser;
        Identifiers = new PostgresIdentifierPolicy(parser);
    }

    public async Task<IProviderConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return new PostgresConnection(conn, connectionString);
    }

    private sealed class PostgresConnection(NpgsqlConnection conn, string connectionString) : IProviderConnection
    {
        // Metadata gets its own connection: this one may be streaming a query or holding an open transaction,
        // and Npgsql allows one command at a time per connection.
        public async Task<DatabaseSchema> IntrospectAsync(CancellationToken ct)
        {
            await using var meta = new NpgsqlConnection(connectionString);
            await meta.OpenAsync(ct);
            return await PostgresIntrospector.ReadAsync(meta, ct);
        }

        public async Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
        {
            var cmd = new NpgsqlCommand(sql, conn);
            return await cmd.ExecuteReaderAsync(ct);
        }

        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }
}
