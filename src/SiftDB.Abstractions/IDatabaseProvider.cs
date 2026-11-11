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
    Task<IProviderConnection> OpenAsync(string connectionString, CancellationToken ct);
}

public interface IProviderConnection : IAsyncDisposable
{
    /// <summary>Starts a query and returns a forward-only reader. Rows must stream; never buffer the whole result.</summary>
    Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct);
}
