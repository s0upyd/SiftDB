using System.Collections.Concurrent;
using System.Text.Json;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// The schema explorer's details and scripts against a real server (design section 14, F13). Needs SIFTDB_SPIKE_CONN.
/// Creates a uniquely named schema, dropped at the end.
/// </summary>
public class SchemaExplorerTests : IAsyncLifetime
{
    private sealed class Sink : IEngineOutput
    {
        private readonly ConcurrentQueue<string> _events = new();
        public void SendEvent(string json) => _events.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch b) { }
        public JsonElement Reply(int id) => _events.Select(e => JsonDocument.Parse(e).RootElement.Clone())
            .Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private readonly string? _cs = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN");
    private readonly string _s = "siftdb_ex_" + Guid.NewGuid().ToString("N")[..8];
    private readonly Sink _sink = new();
    private EngineHost? _engine;
    private int _request, _conn;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_cs)) return;
        await Outside($"""
            create schema {_s};
            set search_path = {_s};
            create type status as enum ('new', 'paid', 'shipped');
            create domain positive as numeric check (value > 0);
            create table "Customer" (
                id int generated always as identity primary key,
                email text not null unique,
                name text default 'anon'
            );
            comment on table "Customer" is 'People who buy';
            comment on column "Customer".email is 'Where invoices go';
            create table orders (
                id bigint primary key,
                customer_id int not null references "Customer" on delete cascade,
                state status not null default 'new',
                total positive,
                doubled numeric generated always as (total * 2) stored,
                spot box,
                check (id > 0),
                exclude using gist (spot with &&)
            );
            create index orders_open on orders (customer_id) where state <> 'shipped';
            create function touch() returns trigger language plpgsql as $$ begin return new; end $$;
            create trigger orders_touch before insert or update on orders for each row execute function touch();
            create table invoices (id int primary key, customer_email text references "Customer" (email) deferrable);
            create table notes (
                title text not null check (char_length(title) between 1 and 80),
                "Body" text constraint body_len check (length("Body") < 2000),
                tag varchar check (length(tag) <= 20 or tag is null),
                plain text
            );
            create table events (at date not null, what text) partition by range (at);
            create table events_2026 partition of events for values from ('2026-01-01') to ('2027-01-01');
            create view big_orders as select id, total from orders where total > 100;
            create materialized view totals as select customer_id, sum(total) as total from orders group by customer_id;
            create index totals_customer on totals (customer_id);
            create sequence ticket_seq as integer start 1000;
            create function add_one(n integer) returns integer language sql as 'select n + 1';
            create function add_one(n bigint) returns bigint language sql as 'select n + 1';
            """);
        _engine = new EngineHost([new PostgresProvider()], _sink);
        _conn = (await Send(new { type = "connect", provider = "postgres", connectionString = _cs })).GetProperty("connectionId").GetInt32();
        await Send(new { type = "introspect", connectionId = _conn, send = false });
    }

    public async Task DisposeAsync()
    {
        if (_engine is null) return;
        await _engine.DisposeAsync();
        try { await Outside($"drop schema if exists {_s} cascade"); } catch { /* best effort */ }
    }

    private void Require() => Skip.If(string.IsNullOrEmpty(_cs), "SIFTDB_SPIKE_CONN not set");

    private async Task Outside(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<JsonElement> Send(object cmd)
    {
        var id = ++_request;
        var json = JsonSerializer.SerializeToNode(cmd)!.AsObject();
        json["id"] = id;
        await _engine!.HandleCommandAsync(json.ToJsonString());
        return _sink.Reply(id);
    }

    private async Task<JsonElement> Detail(string name)
    {
        var r = await Send(new { type = "relationDetail", connectionId = _conn, schema = _s, name });
        Assert.True(r.GetProperty("found").GetBoolean(), $"{name} not found: {r}");
        return r.GetProperty("detail");
    }

    private static List<JsonElement> Arr(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().ToList();

    [SkippableFact]
    public async Task A_table_has_its_comment_keys_identity_and_what_references_it_through()
    {
        Require();
        var d = await Detail("Customer");
        Assert.Equal("People who buy", d.GetProperty("comment").GetString());
        Assert.True(d.GetProperty("totalBytes").GetInt64() > 0);
        var cols = Arr(d, "columns");
        Assert.Equal("always", cols[0].GetProperty("identity").GetString());
        Assert.Equal("Where invoices go", cols[1].GetProperty("comment").GetString());
        var keys = Arr(d, "keys");
        Assert.True(keys[0].GetProperty("primary").GetBoolean());                     // the primary key first
        Assert.Equal(["email"], Arr(keys[1], "columns").Select(c => c.GetString()));
        Assert.Equal("UNIQUE (email)", keys[1].GetProperty("definition").GetString());
        var quoted = (await Send(new { type = "relationDetail", connectionId = _conn, schema = _s, name = "Customer" })).GetProperty("quoted");
        Assert.Equal("\"Customer_pkey\"", quoted.GetProperty("Customer_pkey").GetString()); // mixed case: quoted to drag
    }

    [SkippableFact]
    public async Task A_length_check_shows_on_its_column()
    {
        Require();
        var cols = Arr(await Detail("notes"), "columns").ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal(80, cols["title"].GetProperty("maxLength").GetInt32());
        Assert.Equal(1999, cols["Body"].GetProperty("maxLength").GetInt32());          // < 2000, a quoted name
        Assert.Equal("body_len", cols["Body"].GetProperty("lengthCheck").GetString());
        Assert.Equal(JsonValueKind.Null, cols["tag"].GetProperty("maxLength").ValueKind); // OR: not read as a limit
        Assert.Equal(JsonValueKind.Null, cols["plain"].GetProperty("maxLength").ValueKind);
    }

    [Theory]
    [InlineData("CHECK (length(a) <= 100)", "a", 100)]
    [InlineData("CHECK (char_length(a) < 50)", "a", 49)]
    [InlineData("CHECK (length(a::text) <= 20)", "a", 20)]
    [InlineData("CHECK (character_length(a) <= 5)", "a", 5)]
    [InlineData("CHECK (100 >= length(a))", "a", 100)]
    [InlineData("CHECK (length(a) >= 1 AND length(a) <= 30)", "a", 30)]
    [InlineData("CHECK (char_length(a) <= 10 AND a <> ''::text)", "a", 10)]
    [InlineData("CHECK (length(\"My \"\"Col\"\"\") <= 7)", "My \"Col\"", 7)]
    public void Length_limits_are_read_from_a_check(string definition, string column, int max) =>
        Assert.Equal(max, PostgresDetail.LengthLimits(definition)[column]);

    [Theory]
    [InlineData("CHECK (length(a) <= 10 OR a IS NULL)")]
    [InlineData("CHECK (NOT length(a) <= 10)")]
    [InlineData("CHECK (length(a) >= 3)")]
    [InlineData("CHECK (a > 0)")]
    public void Checks_that_do_not_cap_the_length_give_none(string definition) => Assert.Empty(PostgresDetail.LengthLimits(definition));

    [Fact]
    public void Each_column_of_a_check_gets_its_own_limit() =>
        Assert.Equal(new Dictionary<string, int> { ["g"] = 7, ["h"] = 8 }, PostgresDetail.LengthLimits("CHECK (length(g) <= 7 AND length(h) <= 8)"));

    [SkippableFact]
    public async Task Constraints_indexes_triggers_and_generated_columns_are_described()
    {
        Require();
        var d = await Detail("orders");
        var fk = Assert.Single(Arr(d, "foreignKeys"));
        Assert.Equal("cascade", fk.GetProperty("onDelete").GetString());
        Assert.Equal(JsonValueKind.Null, fk.GetProperty("onUpdate").ValueKind);   // the default is not worth saying
        Assert.Equal("Customer", fk.GetProperty("referencedRelation").GetString());
        // Postgres's own text for the expression (total is a domain over numeric, so it is cast).
        Assert.Matches(@"total.*\*.*2", Arr(d, "columns").Single(c => c.GetProperty("name").GetString() == "doubled").GetProperty("generated").GetString());

        var checks = Arr(d, "checks");
        Assert.Contains(checks, c => !c.GetProperty("exclusion").GetBoolean() && c.GetProperty("definition").GetString() == "CHECK (id > 0)");
        Assert.Contains(checks, c => c.GetProperty("exclusion").GetBoolean());

        var indexes = Arr(d, "indexes");
        Assert.True(indexes[0].GetProperty("primary").GetBoolean());
        var partial = indexes.Single(i => i.GetProperty("name").GetString() == "orders_open");
        Assert.Equal("btree", partial.GetProperty("method").GetString());
        Assert.Equal("customer_id", partial.GetProperty("keys").GetString());
        Assert.Equal("state <> 'shipped'::" + _s + ".status", partial.GetProperty("predicate").GetString());
        Assert.Contains(indexes, i => i.GetProperty("method").GetString() == "gist" && i.GetProperty("constraint").ValueKind == JsonValueKind.String);

        var trigger = Assert.Single(Arr(d, "triggers"));
        Assert.Equal("before", trigger.GetProperty("timing").GetString());
        Assert.Equal(["insert", "update"], Arr(trigger, "events").Select(e => e.GetString()));
        Assert.True(trigger.GetProperty("forEachRow").GetBoolean());
        Assert.Equal($"{_s}.touch", trigger.GetProperty("function").GetString());

        var invoices = await Detail("invoices");
        Assert.True(Assert.Single(Arr(invoices, "foreignKeys")).GetProperty("deferrable").GetBoolean());
    }

    [SkippableFact]
    public async Task Partitions_and_view_definitions()
    {
        Require();
        var p = Assert.Single(Arr(await Detail("events"), "partitions"));
        Assert.Equal("events_2026", p.GetProperty("name").GetString());
        Assert.Equal("FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')", p.GetProperty("bound").GetString());
        Assert.StartsWith("SELECT id,", (await Detail("big_orders")).GetProperty("definition").GetString());
        Assert.Equal(JsonValueKind.Null, (await Detail("big_orders")).GetProperty("totalBytes").ValueKind); // a view has no size
    }

    [SkippableFact]
    public async Task A_schemas_functions_sequences_and_types()
    {
        Require();
        var o = (await Send(new { type = "schemaObjects", connectionId = _conn, schema = _s })).GetProperty("objects");
        var fns = Arr(o, "functions");
        Assert.Equal(["integer", "bigint"], fns.Where(f => f.GetProperty("name").GetString() == "add_one").Select(f => f.GetProperty("returns").GetString()).Order().Reverse());
        Assert.Contains(fns, f => f.GetProperty("arguments").GetString() == "n integer");
        var seq = Assert.Single(Arr(o, "sequences"), s => s.GetProperty("name").GetString() == "ticket_seq");
        Assert.Equal("integer", seq.GetProperty("dataType").GetString());
        var types = Arr(o, "types");
        Assert.Equal(["new", "paid", "shipped"], Arr(types.Single(t => t.GetProperty("name").GetString() == "status"), "values").Select(v => v.GetString()));
        Assert.Equal("numeric CHECK (VALUE > 0::numeric)", types.Single(t => t.GetProperty("name").GetString() == "positive").GetProperty("detail").GetString());
    }

    private async Task<string> Script(string kind, string name, string script, string? arguments = null)
    {
        var r = await Send(new { type = "scriptObject", connectionId = _conn, kind, schema = _s, name, script, arguments });
        Assert.Equal("script", r.GetProperty("type").GetString());
        return r.GetProperty("sql").GetString()!;
    }

    /// <summary>
    /// The CREATE script rebuilds what it describes: drop the object (with CASCADE here, so what depends on it goes too;
    /// the DROP script itself never cascades) and run the script, in a transaction that is rolled back.
    /// </summary>
    private async Task Rebuilds(string drop, string create)
    {
        await using var c = new Npgsql.NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var cmd = new Npgsql.NpgsqlCommand(drop.TrimEnd(';') + " CASCADE;\n" + create, c, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.RollbackAsync();
    }

    [SkippableTheory]
    [InlineData("relation", "orders")]
    [InlineData("relation", "Customer")]
    [InlineData("relation", "events")]
    [InlineData("relation", "big_orders")]
    [InlineData("relation", "totals")]
    [InlineData("sequence", "ticket_seq")]
    [InlineData("type", "status")]
    [InlineData("type", "positive")]
    public async Task Create_scripts_rebuild_the_object(string kind, string name)
    {
        Require();
        var create = await Script(kind, name, "create");
        var drop = await Script(kind, name, "drop");
        Assert.DoesNotContain("CASCADE", drop); // a DROP that goes further than asked is never written for the user
        await Rebuilds(drop, create);
    }

    [SkippableFact]
    public async Task A_function_script_picks_the_overload()
    {
        Require();
        var create = await Script("function", "add_one", "create", "n bigint");
        Assert.Contains("bigint", create);
        Assert.Equal($"DROP FUNCTION {_s}.add_one(n bigint);", await Script("function", "add_one", "drop", "n bigint"));
        await Rebuilds($"DROP FUNCTION {_s}.add_one(n bigint);", create);
    }

    [SkippableFact]
    public async Task A_select_script_names_every_column()
    {
        Require();
        Assert.Equal($"select id, email, name\nfrom {_s}.\"Customer\";", await Script("relation", "Customer", "select"));
    }
}
