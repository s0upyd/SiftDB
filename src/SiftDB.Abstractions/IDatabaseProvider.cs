using System.Data.Common;

namespace SiftDB.Abstractions;

public static class ProviderContract
{
    /// <summary>Current version of the provider contract (IDatabaseProvider v1).</summary>
    public const int CurrentVersion = 1;
}

public interface IDatabaseProvider
{
    string Id { get; }
    int ContractVersion { get; }

    /// <summary>The dialect's lexer, statement splitter and grammar check.</summary>
    ISqlParser Parser { get; }

    /// <summary>The dialect's identifier quoting rules.</summary>
    IIdentifierPolicy Identifiers { get; }

    Task<IProviderConnection> OpenAsync(string connectionString, CancellationToken ct);
}

public interface IProviderConnection : IAsyncDisposable
{
    /// <summary>Starts a query and returns a forward-only reader. Rows must stream; never buffer the whole result.</summary>
    Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct);

    /// <summary>
    /// Reads the database structure. Must not disturb this connection's session (open transaction, running
    /// query, SET values), so providers do it on a separate connection.
    /// </summary>
    Task<DatabaseSchema> IntrospectAsync(CancellationToken ct);
}
