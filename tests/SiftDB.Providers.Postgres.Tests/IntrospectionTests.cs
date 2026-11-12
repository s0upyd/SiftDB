using Npgsql;
using SiftDB.Abstractions;
using Xunit.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Runs against a real server (SIFTDB_SPIKE_CONN); each test builds and drops its own throwaway schema.</summary>
public class IntrospectionTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ITestOutputHelper _output = output;
    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _s = "siftdb_it_" + Guid.NewGuid().ToString("N")[..8];
    private IProviderConnection? _conn;
    private DatabaseSchema? _schema;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            create schema {_s};
            create schema {_s}_empty;
            create table {_s}."Customers" (
                id serial primary key,
                email varchar(50) not null unique,
                "FullName" text);
            create table {_s}."OrderItems" (
                order_id int not null,
                line int not null,
                sku text,
                qty numeric(10,2) default 1,
                customer_id int references {_s}."Customers"(id),
                primary key (order_id, line));
            create table {_s}.shipments (
                order_id int, line int,
                foreign key (order_id, line) references {_s}."OrderItems"(order_id, line));
            create index ix_sku on {_s}."OrderItems" (sku, qty);
            create index ix_expr on {_s}."OrderItems" (lower(sku));
            create view {_s}.v_items as select order_id, sku from {_s}."OrderItems";
            create materialized view {_s}.mv_items as select order_id from {_s}."OrderItems";
            create table {_s}.events (id int, at timestamptz) partition by range (at);
            create table {_s}.events_2024 partition of {_s}.events for values from ('2024-01-01') to ('2025-01-01');
            """, c);
        await cmd.ExecuteNonQueryAsync();
        _conn = await new PostgresProvider().OpenAsync(_cs, ConnectionOptions.Default, CancellationToken.None);
        _schema = await _conn.IntrospectAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_conn is not null) await _conn.DisposeAsync();
        if (string.IsNullOrEmpty(_cs)) return;
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"drop schema if exists {_s} cascade; drop schema if exists {_s}_empty cascade;", c);
        await cmd.ExecuteNonQueryAsync();
    }

    private void RequireServer() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");
    private SchemaInfo Mine => _schema!.Schemas.Single(s => s.Name == _s);
    private RelationInfo Rel(string name) => Mine.Relations.Single(r => r.Name == name);

    [SkippableFact]
    public void Leaves_out_system_schemas_but_keeps_empty_user_schemas()
    {
        RequireServer();
        var names = _schema!.Schemas.Select(s => s.Name).ToList();
        Assert.DoesNotContain("pg_catalog", names);
        Assert.DoesNotContain("information_schema", names);
        Assert.DoesNotContain(names, n => n.StartsWith("pg_"));
        Assert.Empty(_schema.Schemas.Single(s => s.Name == _s + "_empty").Relations);
    }

    [SkippableFact]
    public void Lists_relations_with_kinds_exact_names_and_hides_partitions()
    {
        RequireServer();
        var kinds = Mine.Relations.ToDictionary(r => r.Name, r => r.Kind);
        Assert.Equal(RelationKind.Table, kinds["Customers"]);   // mixed case preserved
        Assert.Equal(RelationKind.Table, kinds["OrderItems"]);
        Assert.Equal(RelationKind.Table, kinds["shipments"]);
        Assert.Equal(RelationKind.View, kinds["v_items"]);
        Assert.Equal(RelationKind.MaterializedView, kinds["mv_items"]);
        Assert.Equal(RelationKind.PartitionedTable, kinds["events"]);
        Assert.DoesNotContain("events_2024", kinds.Keys);
    }

    [SkippableFact]
    public void Describes_columns_in_order_with_types_nullability_and_defaults()
    {
        RequireServer();
        var customers = Rel("Customers").Columns;
        Assert.Equal(["id", "email", "FullName"], customers.Select(c => c.Name));

        Assert.Equal("integer", customers[0].DataType);
        Assert.False(customers[0].IsNullable);
        Assert.StartsWith("nextval(", customers[0].Default);

        Assert.Equal("character varying(50)", customers[1].DataType);
        Assert.False(customers[1].IsNullable);
        Assert.Null(customers[1].Default);

        Assert.Equal("text", customers[2].DataType);
        Assert.True(customers[2].IsNullable);

        var qty = Rel("OrderItems").Columns.Single(c => c.Name == "qty");
        Assert.Equal("numeric(10,2)", qty.DataType);
        Assert.Equal("1", qty.Default);
    }

    [SkippableFact]
    public void Reads_views_columns_but_no_keys()
    {
        RequireServer();
        var v = Rel("v_items");
        Assert.Equal(["order_id", "sku"], v.Columns.Select(c => c.Name));
        Assert.Empty(v.PrimaryKey);
        Assert.Empty(v.ForeignKeys);
    }

    [SkippableFact]
    public void Reads_primary_keys_in_key_order()
    {
        RequireServer();
        Assert.Equal(["id"], Rel("Customers").PrimaryKey);
        Assert.Equal(["order_id", "line"], Rel("OrderItems").PrimaryKey);
        Assert.Empty(Rel("shipments").PrimaryKey);
    }

    [SkippableFact]
    public void Reads_single_and_composite_foreign_keys()
    {
        RequireServer();
        var single = Rel("OrderItems").ForeignKeys.Single();
        Assert.Equal(["customer_id"], single.Columns);
        Assert.Equal(_s, single.ReferencedSchema);
        Assert.Equal("Customers", single.ReferencedRelation);
        Assert.Equal(["id"], single.ReferencedColumns);

        var composite = Rel("shipments").ForeignKeys.Single();
        Assert.Equal(["order_id", "line"], composite.Columns);
        Assert.Equal("OrderItems", composite.ReferencedRelation);
        Assert.Equal(["order_id", "line"], composite.ReferencedColumns);
    }

    [SkippableFact]
    public void Reads_indexes_including_unique_and_expression_indexes()
    {
        RequireServer();
        var items = Rel("OrderItems").Indexes.ToDictionary(i => i.Name);
        Assert.Equal(["sku", "qty"], items["ix_sku"].Columns);
        Assert.False(items["ix_sku"].IsUnique);
        Assert.Empty(items["ix_expr"].Columns);                       // expression members are omitted
        Assert.True(items["OrderItems_pkey"].IsUnique);
        Assert.Equal(["order_id", "line"], items["OrderItems_pkey"].Columns);

        var email = Rel("Customers").Indexes.Single(i => i.Columns.SequenceEqual(["email"]));
        Assert.True(email.IsUnique);
    }

    [SkippableFact]
    public async Task Scales_to_thousands_of_tables_in_a_handful_of_round_trips()
    {
        RequireServer();
        var big = _s + "_big";
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        try
        {
            await using (var cmd = new NpgsqlCommand($"create schema {big}; create table {big}.base (id int primary key);", c))
                await cmd.ExecuteNonQueryAsync();
            // Batches of 200: one transaction creating thousands of tables exhausts max_locks_per_transaction.
            for (var from = 1; from <= 2000; from += 200)
            {
                await using var cmd = new NpgsqlCommand($"""
                    do $$ begin
                      for i in {from}..{from + 199} loop
                        execute format('create table {big}.t%s (id serial primary key, base_id int references {big}.base(id), '
                          || 'a text, b text, c int, d int, e numeric(10,2), f timestamptz, g boolean, h text, i text, j text)', i);
                        execute format('create index on {big}.t%s (a, b)', i);
                      end loop;
                    end $$;
                    """, c);
                await cmd.ExecuteNonQueryAsync();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var schema = await _conn!.IntrospectAsync(CancellationToken.None);
            var ms = sw.Elapsed.TotalMilliseconds;

            var rels = schema.Schemas.Single(s => s.Name == big).Relations;
            Assert.Equal(2001, rels.Count);
            Assert.All(rels.Where(r => r.Name != "base"), r => Assert.Equal(12, r.Columns.Count));
            Assert.Equal(2000, rels.Count(r => r.ForeignKeys.Count == 1));
            _output.WriteLine($"introspected {rels.Count} tables, {rels.Sum(r => r.Columns.Count)} columns in {ms:F0} ms");
            Assert.True(ms < 5000, $"took {ms:F0} ms");
        }
        finally
        {
            await using var c2 = new NpgsqlConnection(_cs);
            await c2.OpenAsync();
            // Same lock limit applies to dropping: remove the tables in batches, then the schema.
            for (var from = 1; from <= 2000; from += 200)
            {
                await using var batch = new NpgsqlCommand($"""
                    do $$ begin
                      for i in {from}..{from + 199} loop
                        execute format('drop table if exists {big}.t%s', i);
                      end loop;
                    end $$;
                    """, c2);
                await batch.ExecuteNonQueryAsync();
            }
            await using var drop = new NpgsqlCommand($"drop schema if exists {big} cascade", c2);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Does_not_disturb_a_query_that_is_streaming_on_the_same_connection()
    {
        RequireServer();
        await using var conn = await new PostgresProvider().OpenAsync(_cs!, ConnectionOptions.Default, CancellationToken.None);
        await using var reader = await conn.ExecuteReaderAsync("select generate_series(1, 50000)", CancellationToken.None);
        await reader.ReadAsync();

        var schema = await conn.IntrospectAsync(CancellationToken.None); // would throw if it reused the busy connection

        Assert.Contains(schema.Schemas, s => s.Name == _s);
        var rows = 1;
        while (await reader.ReadAsync()) rows++;
        Assert.Equal(50000, rows);
    }
}
