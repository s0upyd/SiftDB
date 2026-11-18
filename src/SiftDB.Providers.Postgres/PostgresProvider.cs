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

    public IReadOnlyList<WholeTableChange> ChangesEveryRow(string sql) => PostgresWholeTableRules.Find(sql);

    public GenericType GenericTypeOf(string dataTypeName) => PostgresTypes.GenericTypeOf(dataTypeName);

    private readonly PostgresSettings _settings = new();

    /// <summary>Postgres refuses <c>SET a.col</c> and <c>boolean = 1</c>, with messages that do not say why.</summary>
    public DialectRules Dialect { get; } = new(QualifiedSetColumns: false, NumbersAsBooleans: false);

    public IReadOnlyList<ProviderSetting> Settings => PostgresSettings.All;

    public void ApplySettings(IReadOnlyDictionary<string, string> values) => _settings.Apply(values);

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
        return new PostgresConnection(conn, cs, _settings);
    }

    private sealed class PostgresConnection(NpgsqlConnection conn, string connectionString, PostgresSettings settings) : IProviderConnection
    {
        public ITransactionMonitor Transactions { get; } = new PostgresTransactionMonitor(conn, settings);

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

        // On the session itself, so its temp tables and the tables its open transaction created are seen, and a bare name
        // resolves as the statement's will (pg_table_is_visible follows the search path, pg_temp first). Only catalog reads
        // with parameters, so it cannot fail inside a healthy transaction; in an aborted one the server refuses, and the
        // answer is "don't know". reltuples is -1 for a table never vacuumed or analysed: no estimate then.
        public async Task<TableFacts?> DescribeTableAsync(string? schema, string table, CancellationToken ct)
        {
            const string sql = """
                select c.relpersistence = 't', c.reltuples::float8
                from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace
                where c.relname = @table
                  and case when @schema is null then pg_catalog.pg_table_is_visible(c.oid) else n.nspname = @schema end
                limit 1
                """;
            try
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.Add(new NpgsqlParameter("table", NpgsqlTypes.NpgsqlDbType.Text) { Value = table });
                cmd.Parameters.Add(new NpgsqlParameter("schema", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)schema ?? DBNull.Value });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;
                var rows = r.GetDouble(1);
                return new TableFacts(r.GetBoolean(0), rows >= 0 ? (long)Math.Round(rows) : null);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }
}
