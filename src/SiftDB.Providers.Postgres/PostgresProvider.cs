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

    public bool? SameMeaning(string a, string b) => PostgresSameMeaning.Same(a, b);

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
            if (IsPasswordProblem(ex)) throw new ConnectionAuthenticationException(ex.Message, ex);
            throw;
        }
        return new PostgresConnection(conn, cs, _settings, (PostgresSqlParser)Parser, Identifiers);
    }

    /// <summary>28P01: wrong password. Or Npgsql's own message when the server wants a password and none was given.</summary>
    private static bool IsPasswordProblem(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword } ||
        ex is NpgsqlException && ex.Message.StartsWith("No password has been provided", StringComparison.Ordinal);

    /// <summary>
    /// Asks pg_database, logged in to the database the form names if it is there, else to "postgres" or "template1", which
    /// nearly every server has: the point is that the user need not know a database name yet. Not pooled, so the form
    /// leaves no idle connections behind.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListDatabasesAsync(string connectionString, CancellationToken ct)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        string?[] candidates = [string.IsNullOrEmpty(b.Database) ? null : b.Database, "postgres", "template1"];
        Exception? last = null;
        foreach (var db in candidates.Where(d => d is not null).Distinct())
        {
            b.Database = db;
            await using var conn = new NpgsqlConnection(b.ConnectionString);
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidCatalogName or PostgresErrorCodes.InsufficientPrivilege)
            {
                last = ex; // no such database, or no CONNECT on it: try the next
                continue;
            }
            catch (Exception ex) when (IsPasswordProblem(ex))
            {
                throw new ConnectionAuthenticationException(ex.Message, ex);
            }
            await using var cmd = new NpgsqlCommand(
                "select datname from pg_database where datallowconn and not datistemplate and has_database_privilege(datname, 'CONNECT') order by datname",
                conn);
            var names = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
            return names;
        }
        throw last!;
    }

    private sealed class PostgresConnection : IProviderConnection
    {
        private readonly NpgsqlConnection conn;
        private readonly string connectionString;
        private readonly PostgresTransactionMonitor _monitor;

        private readonly IIdentifierPolicy _policy;

        public PostgresConnection(NpgsqlConnection conn, string connectionString, PostgresSettings settings, PostgresSqlParser parser, IIdentifierPolicy policy)
        {
            this.conn = conn;
            this.connectionString = connectionString;
            _policy = policy;
            _monitor = new PostgresTransactionMonitor(conn, settings);
            DryRun = new PostgresDryRun(conn, parser, _monitor);
            Plans = new PostgresPlans(conn);
            // The backend's process id is known from the start-up handshake; reading it never touches the session.
            Locks = new PostgresLocks(connectionString, () => conn.ProcessID);
        }

        public ITransactionMonitor Transactions => _monitor;

        public ILockInspector Locks { get; }

        public IDryRunSupport DryRun { get; }

        public IPlanSupport Plans { get; }

        // Metadata gets its own connection: this one may be streaming a query or holding an open transaction,
        // and Npgsql allows one command at a time per connection.
        public async Task<DatabaseSchema> IntrospectAsync(CancellationToken ct)
        {
            await using var meta = new NpgsqlConnection(connectionString);
            await meta.OpenAsync(ct);
            var origins = new Dictionary<uint, PostgresIntrospector.TableNames>();
            var schema = await PostgresIntrospector.ReadAsync(meta, ct, origins);
            _origins = origins; // replaced whole, so a result being described never sees half of it
            return schema;
        }

        /// <summary>Every relation's names by oid, from the last introspection: what a row description's oids mean.</summary>
        private volatile IReadOnlyDictionary<uint, PostgresIntrospector.TableNames> _origins = new Dictionary<uint, PostgresIntrospector.TableNames>();

        // Following foreign keys (design section 14, F4). No query: the row description says which table oid and column
        // number each column comes from, and the last introspection says what they are called. A table created since then
        // has no origin until the schema is refreshed.
        public IReadOnlyList<ColumnOrigin?> ColumnOrigins(DbDataReader reader)
        {
            if (PostgresRowDescription.Read(reader) is not { } fields) return [];
            var map = _origins;
            return fields.Select(f => f.Table != 0 && f.Column > 0 && map.TryGetValue(f.Table, out var t) && t.Columns.TryGetValue(f.Column, out var c)
                ? new ColumnOrigin(t.Schema, t.Table, c) : null).ToList();
        }

        private const string PeekSavepoint = "siftdb_peek";

        /// <summary>
        /// On the idle session, so its own uncommitted rows are seen. Inside a transaction the lookup runs behind a
        /// savepoint: it only reads, so it changes nothing, but a value the column's type refuses would otherwise abort the
        /// user's transaction. In an aborted transaction, or when the session is busy, a side connection is used.
        /// </summary>
        public async Task<LookedUpRow?> PeekRowAsync(RowKey key, bool sessionIdle, CancellationToken ct)
        {
            var sql = $"select * from {Quote(key.Schema)}.{Quote(key.Table)} where {Where(key)} limit 1";
            var status = sessionIdle ? _monitor.ProtocolStatus : "";
            if (status is "Idle" or "InTransactionBlock")
            {
                var guarded = status == "InTransactionBlock";
                if (guarded) await _monitor.SendAsync($"savepoint {PeekSavepoint}", ct);
                try
                {
                    var row = await ReadRowAsync(conn, sql, key, committedOnly: false, ct);
                    if (guarded) await _monitor.SendAsync($"release savepoint {PeekSavepoint}", ct);
                    return row;
                }
                catch when (guarded)
                {
                    await _monitor.SendAsync($"rollback to savepoint {PeekSavepoint}; release savepoint {PeekSavepoint}", CancellationToken.None);
                    throw;
                }
            }
            await using var side = new NpgsqlConnection(connectionString);
            await side.OpenAsync(ct);
            return await ReadRowAsync(side, sql, key, committedOnly: true, ct);
        }

        public async Task<long?> CountRowsAsync(RowKey key, long cap, TimeSpan timeout, CancellationToken ct)
        {
            await using var side = new NpgsqlConnection(connectionString);
            await side.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                $"select count(*) from (select 1 from {Quote(key.Schema)}.{Quote(key.Table)} where {Where(key)} limit {cap + 1}) s", side)
            {
                CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)),
            };
            AddValues(cmd, key);
            try { return (long)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (Exception ex) when (ex is NpgsqlException { InnerException: TimeoutException } or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
            {
                return null; // too slow to count: the caller says so rather than waiting
            }
        }

        private static async Task<LookedUpRow?> ReadRowAsync(NpgsqlConnection c, string sql, RowKey key, bool committedOnly, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(sql, c);
            AddValues(cmd, key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var names = new string[r.FieldCount];
            var values = new object?[r.FieldCount];
            var types = new string[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
            {
                names[i] = r.GetName(i);
                values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                types[i] = r.GetDataTypeName(i);
            }
            return new LookedUpRow(names, values, types, committedOnly);
        }

        // The schema explorer (design section 14, F13): each on a connection of its own, like the fast schema load, so the
        // session (possibly streaming, possibly in a transaction) is never touched.
        public async Task<RelationDetail?> DescribeRelationAsync(string schema, string relation, CancellationToken ct)
        {
            await using var meta = new NpgsqlConnection(connectionString);
            await meta.OpenAsync(ct);
            return await PostgresDetail.RelationAsync(meta, schema, relation, ct);
        }

        public async Task<SchemaObjects?> DescribeSchemaObjectsAsync(string schema, CancellationToken ct)
        {
            await using var meta = new NpgsqlConnection(connectionString);
            await meta.OpenAsync(ct);
            return await PostgresDetail.ObjectsAsync(meta, schema, ct);
        }

        public async Task<string?> ScriptAsync(ObjectRef obj, bool create, CancellationToken ct)
        {
            await using var meta = new NpgsqlConnection(connectionString);
            await meta.OpenAsync(ct);
            return await PostgresDetail.ScriptAsync(meta, _policy, obj, create, ct);
        }

        private static string Where(RowKey key) => string.Join(" and ", key.Columns.Select((c, i) => $"{Quote(c)} = @p{i}"));

        /// <summary>The values go as text of no declared type, so the server reads each as its column's own type.</summary>
        private static void AddValues(NpgsqlCommand cmd, RowKey key)
        {
            for (var i = 0; i < key.Values.Count; i++)
                cmd.Parameters.Add(new NpgsqlParameter($"p{i}", NpgsqlTypes.NpgsqlDbType.Unknown) { Value = key.Values[i] });
        }

        private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

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

        /// <summary>
        /// A session the server has already ended (from another sheet's "End their session", an admin, a restart) cannot be
        /// reset on close: Npgsql's attempt fails on the dead socket. It is closed all the same, and that is not an error.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try { await conn.DisposeAsync(); }
            catch (NpgsqlException) { /* already gone: nothing left to close or roll back */ }
        }
    }
}
