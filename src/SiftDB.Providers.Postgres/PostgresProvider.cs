using System.Data.Common;
using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

public sealed class PostgresProvider : IDatabaseProvider
{
    public string Id => "postgres";
    public string DisplayName => "PostgreSQL";
    public int ContractVersion => ProviderContract.CurrentVersion;
    public ISqlParser Parser { get; }
    public IIdentifierPolicy Identifiers { get; }
    public IReadOnlyList<ConnectionField> ConnectionFields => PostgresConnectionFields.All;
    public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Server;

    public PostgresProvider()
    {
        var parser = new PostgresSqlParser();
        Parser = parser;
        Identifiers = new PostgresIdentifierPolicy(parser);
    }

    public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) =>
        PostgresConnectionFields.Build(fields, password);

    public ParsedConnection ParseConnectionString(string connectionString) => PostgresConnectionFields.Parse(connectionString);

    public string? CheckReadOnly(string sql) => PostgresReadOnlyGuard.Check(sql);

    public bool CannotRunInTransaction(string sql) => PostgresTransactionRules.CannotRunInTransaction(sql);

    public GenericType GenericTypeOf(string dataTypeName) => PostgresTypes.GenericTypeOf(dataTypeName);

    public async Task<IProviderConnection> OpenAsync(string connectionString, ConnectionOptions options, CancellationToken ct)
    {
        var cs = PostgresConnectionFields.WithOptions(connectionString, options);
        var conn = new NpgsqlConnection(cs);
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            await conn.DisposeAsync();
            // 28P01: wrong password. Npgsql's own message when the server wants a password and none was given.
            if (ex is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword } ||
                ex is NpgsqlException && ex.Message.StartsWith("No password has been provided", StringComparison.Ordinal))
                throw new ConnectionAuthenticationException(ex.Message, ex);
            throw;
        }
        return new PostgresConnection(conn, cs);
    }

    private sealed class PostgresConnection(NpgsqlConnection conn, string connectionString) : IProviderConnection
    {
        public ITransactionMonitor Transactions { get; } = new PostgresTransactionMonitor(conn);

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
