using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Postgres types onto the generic ones that result styling uses (design section 10).</summary>
public class GenericTypeTests
{
    [Theory]
    [InlineData("integer", GenericType.Integer)]
    [InlineData("bigint", GenericType.Integer)]
    [InlineData("int2", GenericType.Integer)]
    [InlineData("numeric(10,2)", GenericType.Decimal)]
    [InlineData("double precision", GenericType.Decimal)]
    [InlineData("money", GenericType.Decimal)]
    [InlineData("boolean", GenericType.Boolean)]
    [InlineData("bit(1)", GenericType.Boolean)]
    [InlineData("bit", GenericType.Boolean)]
    [InlineData("bit(8)", GenericType.Text)]
    [InlineData("date", GenericType.Date)]
    [InlineData("timestamp(3) with time zone", GenericType.DateTime)]
    [InlineData("timestamptz", GenericType.DateTime)]
    [InlineData("pg_catalog.int4", GenericType.Integer)]
    [InlineData("integer[]", GenericType.Text)]
    [InlineData("character varying(20)", GenericType.Text)]
    [InlineData("uuid", GenericType.Text)]
    [InlineData("time without time zone", GenericType.Text)]
    [InlineData("public.my_domain", GenericType.Text)]
    public void Names_map_to_generic_types(string name, GenericType expected) =>
        Assert.Equal(expected, PostgresTypes.GenericTypeOf(name));

    private sealed class Sink : IEngineOutput
    {
        public readonly List<string> Events = new();
        public void SendEvent(string json) { lock (Events) Events.Add(json); }
        public void SendBatch(int queryId, EncodedBatch b) { }
    }

    /// <summary>The names Npgsql really reports, through the engine's "columns" event. Needs SIFTDB_SPIKE_CONN.</summary>
    [SkippableFact]
    public async Task A_real_result_reports_each_columns_generic_type()
    {
        var cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
        Skip.If(string.IsNullOrEmpty(cs), "SIFTDB_SPIKE_CONN not set");
        const string sql = """
            select 1::int as i, 2::bigint as b, 1.5::numeric(10,2) as n, 2.5::float8 as f, true as flag, b'1'::bit(1) as bit1,
                   current_date as d, now() as ts, now()::timestamp as tsl, 'x'::text as t, array[1] as arr, gen_random_uuid() as u
            """;
        var sink = new Sink();
        await using var engine = new EngineHost([new PostgresProvider()], sink);
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "connect", id = 1, provider = "postgres", connectionString = cs }));
        await engine.HandleCommandAsync(JsonSerializer.Serialize(new { type = "execute", id = 2, connectionId = 1, sql }));

        string columns;
        lock (sink.Events) columns = sink.Events.Single(e => e.Contains("\"columns\""));
        var types = JsonDocument.Parse(columns).RootElement.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("type").GetString());
        Assert.Equal("integer", types["i"]);
        Assert.Equal("integer", types["b"]);
        Assert.Equal("decimal", types["n"]);
        Assert.Equal("decimal", types["f"]);
        Assert.Equal("boolean", types["flag"]);
        Assert.Equal("boolean", types["bit1"]);
        Assert.Equal("date", types["d"]);
        Assert.Equal("dateTime", types["ts"]);
        Assert.Equal("dateTime", types["tsl"]);
        Assert.Equal("text", types["t"]);
        Assert.Equal("text", types["arr"]);
        Assert.Equal("text", types["u"]);
    }
}
