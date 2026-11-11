using System.Data.Common;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

internal sealed class Capture : IEngineOutput
{
    public readonly List<string> Events = new();
    public void SendEvent(string json) => Events.Add(json);
    public void SendBatch(int queryId, EncodedBatch batch) { }
}

/// <summary>Real Postgres parsing and quoting, with an in-memory schema and no database behind it.</summary>
internal sealed class InMemoryProvider(DatabaseSchema schema) : IDatabaseProvider
{
    private readonly PostgresProvider _pg = new();
    public string Id => "postgres";
    public int ContractVersion => _pg.ContractVersion;
    public ISqlParser Parser => _pg.Parser;
    public IIdentifierPolicy Identifiers => _pg.Identifiers;
    public Task<IProviderConnection> OpenAsync(string cs, CancellationToken ct) => Task.FromResult<IProviderConnection>(new Conn(schema));

    private sealed class Conn(DatabaseSchema schema) : IProviderConnection
    {
        public Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<DatabaseSchema> IntrospectAsync(CancellationToken ct) => Task.FromResult(schema);
    }
}
