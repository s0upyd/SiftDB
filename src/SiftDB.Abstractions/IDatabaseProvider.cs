using System.Data.Common;

namespace SiftDB.Abstractions;

public static class ProviderContract
{
    /// <summary>
    /// Current version of the provider contract. v2 added connection fields, read-only sessions and CannotRunInTransaction;
    /// v3 added GenericTypeOf; v4 added provider settings, the statement guard (ITransactionMonitor.BeforeStatementAsync) and
    /// Dialect; v5 added ChangesEveryRow and IProviderConnection.DescribeTableAsync (the missing-WHERE warning); v6 added
    /// IProviderConnection.DryRun; v7 added SameMeaning (Format SQL); v8 added ColumnOrigins, PeekRowAsync and CountRowsAsync
    /// (following foreign keys); v9 added DescribeRelationAsync, DescribeSchemaObjectsAsync and ScriptAsync (the schema explorer); v10 added ListDatabasesAsync and
    /// ConnectionFieldKind.Database (picking the database in the connection form); v11 added IProviderConnection.Plans
    /// (Visual EXPLAIN); v12 added IProviderConnection.Locks (who's blocking me); v13 added
    /// ColumnDetail.MaxLength and LengthCheck (a length check shown on its column); v14 added IDatabaseProvider.Secret
    /// (the provider names its own secret, and says when it is needed); v15 added WritesSchemaNames, RowLimitClause and
    /// HasTransactions (a database without schemas, LIMIT or transactions: DynamoDB); v16 added
    /// IProviderConnection.FullScansAsync (the scan warning).
    /// </summary>
    public const int CurrentVersion = 16;

    /// <summary>
    /// The oldest contract a provider can be built against and still load (design section 14, F18). It holds because of
    /// one rule: <b>Abstractions only grows.</b> New members get default ("not supported") implementations; existing
    /// members, records' constructors included, are never changed or removed. A change that breaks that rule raises this
    /// number on purpose, and is a <c>!</c> commit. v13 is the floor: it added parameters to ColumnDetail's constructor.
    /// </summary>
    public const int MinimumVersion = 13;

    /// <summary>
    /// Whether a provider built against contract <paramref name="built"/>, which works with this app's contract from
    /// <paramref name="min"/> on, can be loaded here.
    /// </summary>
    public static bool Accepts(int min, int built) => built >= MinimumVersion && min <= CurrentVersion && min <= built;
}

public interface IDatabaseProvider
{
    string Id { get; }

    /// <summary>Shown where a provider is chosen, e.g. "PostgreSQL".</summary>
    string DisplayName { get; }

    int ContractVersion { get; }

    /// <summary>The dialect's lexer, statement splitter and grammar check.</summary>
    ISqlParser Parser { get; }

    /// <summary>The dialect's identifier quoting rules.</summary>
    IIdentifierPolicy Identifiers { get; }

    /// <summary>What a saved connection holds for this provider, in form order.</summary>
    IReadOnlyList<ConnectionField> ConnectionFields { get; }

    /// <summary>
    /// The connections' secret: what it is called and when it is needed; null when they have none. A password, always
    /// needed, by default.
    /// </summary>
    SecretSpec? Secret => new("Password");

    /// <summary>
    /// Whether SQL names a table with its schema (<c>shop.orders</c>). False for a database without schemas (DynamoDB),
    /// whose one schema only groups its tables in the tree and is never written. True by default; even then the schema
    /// called <c>public</c> is left out where a name is written for the user.
    /// </summary>
    bool WritesSchemaNames => true;

    /// <summary>
    /// The clause that limits a SELECT to its first <paramref name="rows"/> rows, written at its end ("Select top 100
    /// rows"), e.g. <c>limit 100</c>; null when the dialect has none (DynamoDB's PartiQL), and then the rows are only
    /// fetched page by page as they are shown. None by default.
    /// </summary>
    string? RowLimitClause(int rows) => null;

    /// <summary>
    /// Whether a session can hold a transaction. False when every statement commits on its own (DynamoDB): there is
    /// then no Begin, no auto-transaction, and the state is always "no transaction". True by default.
    /// </summary>
    bool HasTransactions => true;

    /// <summary>Builds a connection string from saved fields (by <see cref="ConnectionField.Key"/>) and a password.</summary>
    string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password);

    /// <summary>Takes a connection string apart into <see cref="ConnectionFields"/>, for "Save as…". Throws on a malformed string.</summary>
    ParsedConnection ParseConnectionString(string connectionString);

    /// <summary>Whether the server or only the app enforces a read-only session.</summary>
    ReadOnlyEnforcement ReadOnlyEnforcement { get; }

    /// <summary>
    /// On a read-only session, why <paramref name="sql"/> must not run (it would switch read-only off), or null. Recognised
    /// from the statement's structure, not its text. A guard against mistakes, not a security boundary: what happens inside
    /// a function body is not seen.
    /// </summary>
    string? CheckReadOnly(string sql);

    /// <summary>
    /// True when <paramref name="sql"/> (one or more statements) refuses to run inside a transaction block (Postgres:
    /// VACUUM, CREATE DATABASE, CREATE INDEX CONCURRENTLY…). Auto-transaction keeps a transaction open, so it ends an
    /// unchanged one before such a statement, and refuses the statement while there are changes. SQL that does not
    /// parse is false: the server reports it.
    /// </summary>
    bool CannotRunInTransaction(string sql);

    /// <summary>
    /// The tables <paramref name="sql"/> (one or more statements) changes every row of: an UPDATE or DELETE with no WHERE,
    /// or a TRUNCATE. Read from the statement's structure, not its text. SQL that does not parse gives none (the server
    /// reports it). None by default, so a provider without it simply gets no warning.
    /// </summary>
    IReadOnlyList<WholeTableChange> ChangesEveryRow(string sql) => [];

    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> mean exactly the same: the same parse tree, positions aside
    /// (design section 14, F7). Format SQL applies a layout only when this is true. Null when the provider cannot tell,
    /// and then nothing is formatted; SQL that does not parse is false.
    /// </summary>
    bool? SameMeaning(string a, string b) => null;

    /// <summary>
    /// The generic type (design section 10) of a result column, from the name the reader reports for it
    /// (<see cref="DbDataReader.GetDataTypeName"/>). Anything without a closer match is <see cref="GenericType.Text"/>.
    /// </summary>
    GenericType GenericTypeOf(string dataTypeName);

    /// <summary>What the identifier checker should warn about in this dialect. Permissive (no such warnings) by default.</summary>
    DialectRules Dialect => DialectRules.Permissive;

    /// <summary>Settings only this kind of database has (design section 10), shown as its own category. None by default.</summary>
    IReadOnlyList<ProviderSetting> Settings => [];

    /// <summary>
    /// The user's values for <see cref="Settings"/>: one per setting, always one of its choices (the engine fills in
    /// defaults and drops anything else). Called at startup and whenever the settings are saved, so it can arrive while
    /// sessions are open; it applies from their next statement.
    /// </summary>
    void ApplySettings(IReadOnlyDictionary<string, string> values) { }

    Task<IProviderConnection> OpenAsync(string connectionString, ConnectionOptions options, CancellationToken ct);

    /// <summary>
    /// The databases this login can open on the server <paramref name="connectionString"/> points at, sorted, for the
    /// connection form's Database list. The database the string names need not exist: the provider logs in wherever it
    /// can to ask. Throws <see cref="ConnectionAuthenticationException"/> as <see cref="OpenAsync"/> does. Null when not
    /// offered, and then the form only takes a typed name.
    /// </summary>
    Task<IReadOnlyList<string>?> ListDatabasesAsync(string connectionString, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}

public interface IProviderConnection : IAsyncDisposable
{
    /// <summary>Starts a query and returns a forward-only reader. Rows must stream; never buffer the whole result.</summary>
    Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct);

    /// <summary>Transaction control and state for this connection's session.</summary>
    ITransactionMonitor Transactions { get; }

    /// <summary>
    /// Reads the database structure. Must not disturb this connection's session (open transaction, running
    /// query, SET values), so providers do it on a separate connection.
    /// </summary>
    Task<DatabaseSchema> IntrospectAsync(CancellationToken ct);

    /// <summary>
    /// Whether a table is temporary and about how many rows it has, asked on this session (so its own temp tables, and
    /// tables created in its open transaction, are seen). <paramref name="schema"/> null means the name is resolved as the
    /// session would resolve it. Null when the table is not found or the session cannot answer (an aborted transaction).
    /// Must not fail, and must not disturb the session's transaction. Called only while the session is idle.
    /// </summary>
    Task<TableFacts?> DescribeTableAsync(string? schema, string table, CancellationToken ct) => Task.FromResult<TableFacts?>(null);

    /// <summary>Dry runs of UPDATE and DELETE on this session (design section 14, F2), or null when not offered.</summary>
    IDryRunSupport? DryRun => null;

    /// <summary>Query plans on this session (design section 14, F6), or null when not offered.</summary>
    IPlanSupport? Plans => null;

    /// <summary>Who this session waits for, and who waits for it (design section 14, F3), or null when not offered.</summary>
    ILockInspector? Locks => null;

    /// <summary>
    /// Where each column of a result comes from (design section 14, F4), in column order; null for a column that is
    /// computed, or from a table this connection does not know (yet). Must be cheap (no query): it is asked for every
    /// result, as soon as the reader is open. Empty when the provider cannot tell, and then nothing links.
    /// </summary>
    IReadOnlyList<ColumnOrigin?> ColumnOrigins(System.Data.Common.DbDataReader reader) => [];

    /// <summary>
    /// The first row of <see cref="RowKey.Table"/> whose key columns hold the key's values, or null if there is none.
    /// <paramref name="sessionIdle"/>: the caller holds the idle session, so it may be read there (the session's own
    /// uncommitted rows included) as long as nothing about the session changes, even if the lookup fails; otherwise it is
    /// read on a side connection (<see cref="LookedUpRow.CommittedOnly"/>). Null too when not offered.
    /// </summary>
    Task<LookedUpRow?> PeekRowAsync(RowKey key, bool sessionIdle, CancellationToken ct) => Task.FromResult<LookedUpRow?>(null);

    /// <summary>
    /// How many rows of the key's table hold its values, counting no further than <paramref name="cap"/> + 1 (so "more
    /// than cap" can be said), on a side connection. Null when it took longer than <paramref name="timeout"/>, or when
    /// not offered.
    /// </summary>
    Task<long?> CountRowsAsync(RowKey key, long cap, TimeSpan timeout, CancellationToken ct) => Task.FromResult<long?>(null);

    /// <summary>
    /// One relation's details for the schema explorer (design section 14, F13), read on a connection of its own. Null
    /// when the relation is not there, or when not offered (and then the tree shows what the fast load has).
    /// </summary>
    Task<RelationDetail?> DescribeRelationAsync(string schema, string relation, CancellationToken ct) => Task.FromResult<RelationDetail?>(null);

    /// <summary>A schema's functions, sequences and types, read on a connection of their own; null when not offered.</summary>
    Task<SchemaObjects?> DescribeSchemaObjectsAsync(string schema, CancellationToken ct) => Task.FromResult<SchemaObjects?>(null);

    /// <summary>
    /// The statement that creates (<paramref name="create"/>) or drops an object, as the database would write it, for
    /// "Script as" (it is put into the sheet, never run). Null when the provider cannot script that object.
    /// </summary>
    Task<string?> ScriptAsync(ObjectRef obj, bool create, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// The tables (or indexes) <paramref name="sql"/> would read in full, for the scan warning (design section 14, F19):
    /// a DynamoDB SELECT whose WHERE doesn't fix the partition key with <c>=</c> or <c>IN</c>. The provider applies its
    /// own threshold (a scan of a small table is not worth a question). Asked before every run, so it must be quick: it
    /// may describe a table, but never reads items. None by default.
    /// </summary>
    Task<IReadOnlyList<FullScan>> FullScansAsync(string sql, CancellationToken ct) => Task.FromResult<IReadOnlyList<FullScan>>([]);
}
