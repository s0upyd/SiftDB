using System.Data.Common;

namespace SiftDB.Abstractions;

public static class ProviderContract
{
    /// <summary>Current version of the provider contract. v2 added connection fields and read-only sessions.</summary>
    public const int CurrentVersion = 2;
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

    Task<IProviderConnection> OpenAsync(string connectionString, ConnectionOptions options, CancellationToken ct);
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
}
