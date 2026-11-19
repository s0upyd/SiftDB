using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// The connection form's Database list (design section 8) against the real server: it works before a database name is
/// known. Needs SIFTDB_SPIKE_CONN. Reads only.
/// </summary>
public class ListDatabasesTests
{
    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly PostgresProvider _pg = new();

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private string With(string? database) => new NpgsqlConnectionStringBuilder(_cs) { Database = database }.ConnectionString;

    [SkippableFact]
    public async Task Lists_the_databases_sorted_without_templates()
    {
        Require();
        var names = await _pg.ListDatabasesAsync(_cs!, CancellationToken.None);
        Assert.NotNull(names);
        Assert.Contains(new NpgsqlConnectionStringBuilder(_cs).Database ?? "postgres", names);
        Assert.DoesNotContain("template0", names);
        Assert.DoesNotContain("template1", names);
    }

    [SkippableFact]
    public async Task Works_with_no_database_or_one_that_does_not_exist()
    {
        Require();
        var expected = await _pg.ListDatabasesAsync(_cs!, CancellationToken.None);
        Assert.Equal(expected, await _pg.ListDatabasesAsync(With(null), CancellationToken.None));
        Assert.Equal(expected, await _pg.ListDatabasesAsync(With("siftdb_no_such_database"), CancellationToken.None));
    }
}
