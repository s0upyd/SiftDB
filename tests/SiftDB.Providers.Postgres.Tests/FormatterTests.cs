using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Format SQL (design section 14, F7): the layout, and above all the promise that it never changes what runs.</summary>
public class FormatterTests
{
    private static readonly PostgresProvider Pg = new();

    private static string Format(string sql, FormatOptions? o = null) => SqlFormatter.Format(sql, Pg.Parser.Tokenize(sql), o ?? FormatOptions.Default);

    private static readonly FormatOptions Upper = new(KeywordCase.Upper, 2, CommaPlacement.Start, 60);

    // ---- layout ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("select a, b from t where x = 1 and y = 2 order by a",
        "select a, b\nfrom t\nwhere x = 1\n    and y = 2\norder by a")]
    [InlineData("SELECT  a ,b\n\n\nFROM t", "SELECT a, b\nFROM t")]                       // blank lines never survive inside
    [InlineData("select * from (select id, count(*) as n from t group by id) x where n > -1",
        "select *\nfrom (\n    select id, count(*) as n\n    from t\n    group by id\n) x\nwhere n > -1")]
    [InlineData("with a as (select 1), b as (select 2) select * from a, b",
        "with a as (\n    select 1\n),\nb as (\n    select 2\n)\nselect *\nfrom a, b")]
    [InlineData("update t set a = 1, b = now() where id = @id returning *",
        "update t\nset a = 1, b = now()\nwhere id = @id\nreturning *")]
    [InlineData("delete from t using u where u.id = t.id", "delete from t\nusing u\nwhere u.id = t.id")]
    [InlineData("insert into t (a, b) values (1, 'x') on conflict (a) do update set b = excluded.b",
        "insert into t (a, b)\nvalues (1, 'x')\non conflict (a)\ndo update set b = excluded.b")]
    [InlineData("select 1 union all select 2", "select 1\nunion all\nselect 2")]
    [InlineData("select a from t join u on u.id = t.id and u.ok left outer join v using (id) cross join w",
        "select a\nfrom t\njoin u\n    on u.id = t.id\n    and u.ok\nleft outer join v using (id)\ncross join w")]
    public void Lays_out(string sql, string expected) => Assert.Equal(expected, Format(sql));

    [Fact]
    public void A_long_select_list_goes_one_item_per_line()
    {
        // 115 characters on one line: past the default width of 100.
        var sql = "select order_id, customer_id, created_at, shipped_at, total_amount, currency, status, notes, carrier_code from orders";
        Assert.Equal(
            "select\n    order_id,\n    customer_id,\n    created_at,\n    shipped_at,\n    total_amount,\n    currency,\n    status,\n    notes,\n    carrier_code\nfrom orders",
            Format(sql));
        Assert.Equal(
            "SELECT\n  order_id\n  , customer_id\n  , created_at\n  , shipped_at\n  , total_amount\n  , currency\n  , status\n  , notes\n  , carrier_code\nFROM orders",
            Format(sql, Upper));
        // Short enough: one line.
        Assert.Equal("select order_id, customer_id\nfrom orders", Format("select order_id, customer_id from orders"));
    }

    [Theory]
    [InlineData("select a from t where a between 1 and 2 and b = 3", "select a\nfrom t\nwhere a between 1 and 2\n    and b = 3")]
    [InlineData("select a from t where case when x and y then true end and z", "select a\nfrom t\nwhere case when x and y then true end\n    and z")]
    [InlineData("select a from t where a is distinct from b", "select a\nfrom t\nwhere a is distinct from b")] // FROM here is not a clause
    [InlineData("select extract(year from d), count(*) over (partition by a order by b) from t",
        "select extract(year from d), count(*) over (partition by a order by b)\nfrom t")]                      // clauses inside calls stay put
    [InlineData("select x::int, a[1], array[1,2], t.*, -1, @v, - 2 from t",
        "select x::int, a[1], array[1, 2], t.*, -1, @v, - 2\nfrom t")]                                       // a sign is spaced as written
    [InlineData("select a||b, a->>'k', a>=1 from t", "select a || b, a ->> 'k', a >= 1\nfrom t")]
    public void Keeps_what_only_looks_like_a_clause_or_a_list_together(string sql, string expected) => Assert.Equal(expected, Format(sql));

    [Fact]
    public void Comments_stay_where_they_were()
    {
        Assert.Equal("select\n    a, -- first\n    b /* second */\nfrom t -- end\nwhere a = 1",
            Format("select a, -- first\n b /* second */ from t -- end\nwhere a = 1"));
        // With leading commas, a comma followed by a comment stays at the end of its line: the comment belongs to that item.
        Assert.Equal("SELECT\n  a, -- first\n  b\nFROM t", Format("select a, -- first\n b from t", Upper));
    }

    [Fact]
    public void Keyword_case_changes_keywords_only()
    {
        Assert.Equal("SELECT name, \"Type\", 'select' AS x\nFROM t\nWHERE a IS NOT NULL",
            Format("select name, \"Type\", 'select' as x from t where a is not null", Upper));
        Assert.Equal("select name\nfrom t", Format("SELECT name FROM t", Upper with { KeywordCase = KeywordCase.Lower }));
    }

    [Theory]
    [InlineData("create table foo (\n  id int primary key,\n    name text\n)")]
    [InlineData("grant select on t to u")]
    [InlineData("do $$ begin perform 1; end $$")]
    public void Other_statements_are_left_exactly_as_they_are(string sql)
    {
        Assert.Equal(sql, Format(sql));
        Assert.Equal(sql, Format(sql, Upper));
    }

    [Fact]
    public void A_statement_that_starts_indented_keeps_its_indent() =>
        Assert.Equal("select a\n    from t", SqlFormatter.Format("select a from t", Pg.Parser.Tokenize("select a from t"), FormatOptions.Default, baseIndent: 4));

    // ---- the promise: nothing changes but whitespace and keyword case ------------------------------------------------

    public static TheoryData<string> Corpus => new(
        "select 'from where and' as \"select\", $$ select ; from $$ as body, e'\\n' from t",
        "select a /* from */ , b -- where and\n from t",
        "select coalesce((select max(x) from u where u.id = t.id), 0) from t",
        "select * from t where (a = 1 or b = 2) and not exists (select 1 from u) and c in (1, 2, 3)",
        "select a from t where a between symmetric 1 and 2 and b not between 3 and 4",
        "with recursive r(n) as (select 1 union all select n + 1 from r where n < 10) select n from r",
        "with gone as (delete from t where x returning *) insert into log select * from gone",
        "update t set (a, b) = (select a, b from u where u.id = t.id) from u where u.id = t.id",
        "insert into t default values returning id",
        "select a, count(*) from t group by grouping sets ((a), ()) having count(*) > 1 order by 2 desc nulls last limit 10 offset 5",
        "select * from t for update of t skip locked",
        "select data->'a'->>'b', data #> '{a,b}', data @> '{\"a\":1}'::jsonb, x <-> y from t",
        "select - 1, -x, +y, ~z, 2 - -1, a * -b, (-c) from t",
        "select @a, @b + 1 from t where c = @c",
        "select t.\"Mixed Case\", \"OrderItems\".id from \"OrderItems\" join t on true",
        "select array[[1,2],[3,4]], a[1:2], a[:3], (a).b, (f(x)).* from t",
        "select case x when 1 then 'a' when 2 then 'b' else 'c' end, case when a and b then 1 end from t",
        "select string_agg(x, ',' order by y), percentile_cont(0.5) within group (order by v) from t",
        "select row_number() over w from t window w as (partition by a order by b rows between unbounded preceding and current row)",
        "select x from generate_series(1, 10) x cross join lateral (select x * 2 as y) z",
        "values (1, 'a'), (2, 'b')",
        "table orders",
        "explain (analyze, buffers) select * from t where a = 1",
        "select distinct on (a) a, b from t order by a, b desc",
        "select * from t natural full outer join u",
        "select interval '1 day' + now(), date '2026-01-01', cast(x as text), x::numeric(10, 2) from t",
        "select 1 from t where a = any(array[1, 2]) and b <> all(select c from u)",
        "delete from t where current of c",
        "select $1, $2::int from t",
        "select a from t where x like 'a%' escape '\\' and y ilike any (array['%x'])");

    private static readonly FormatOptions[] Options =
    [
        FormatOptions.Default, Upper, new(KeywordCase.Lower, 8, CommaPlacement.End, 40), new(KeywordCase.Upper, 4, CommaPlacement.Start, 400),
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Formatting_never_changes_what_runs(string sql)
    {
        Assert.Null(Pg.Parser.Validate(sql)); // the corpus itself must be valid SQL
        var splitter = StatementSplitter.For(Pg.Parser);
        foreach (var o in Options)
        {
            var f = Format(sql, o);
            Assert.True(SqlFormatter.SameTokens(sql, Pg.Parser.Tokenize(sql), f, Pg.Parser.Tokenize(f)), $"tokens changed:\n{f}");
            Assert.True(Pg.SameMeaning(sql, f), $"meaning changed:\n{f}");
            Assert.Single(splitter.Split(f));
            Assert.DoesNotContain("\n\n", f);                       // a blank line would split it (§6)
            Assert.Equal(f, Format(f, o));                           // formatting twice changes nothing
        }
    }

    // ---- SameMeaning ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("select a from t", "SELECT  A\nFROM T")]
    [InlineData("select array[1,2]", "select array[ 1, 2 ]")]
    [InlineData("select 'x' -- c\n", "select 'x'")]
    public void Same_meaning(string a, string b) => Assert.True(Pg.SameMeaning(a, b));

    [Theory]
    [InlineData("select a from t", "select b from t")]
    [InlineData("select 'x'", "select 'X'")]
    [InlineData("select \"A\" from t", "select A from t")]   // quoted is case-sensitive, bare is folded
    [InlineData("select 1 - -1", "select 1 --1")]            // the second is "select 1" and a comment
    [InlineData("select a from t", "select a from t where")]  // does not parse
    public void Different_meaning(string a, string b) => Assert.False(Pg.SameMeaning(a, b));

    /// <summary>
    /// Every integer field of libpg_query's parse tree named like a position is ignored by SameMeaning. If a later version
    /// adds one, this fails, rather than formatting being refused for no visible reason (or worse, a position deciding).
    /// </summary>
    [Fact]
    public void Every_position_field_of_the_parse_tree_is_ignored()
    {
        var found = new List<string>();
        foreach (var t in typeof(PgSqlParser.Parser).Assembly.GetTypes())
        {
            var d = t.GetProperty("Descriptor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)?.GetValue(null);
            if (d is null || t.Name is "ScanToken" or "ScanResult") continue; // the scanner's start and end are not the tree's
            var fields = d.GetType().GetProperty("Fields")?.GetValue(d);
            if (fields?.GetType().GetMethod("InDeclarationOrder")?.Invoke(fields, null) is not System.Collections.IEnumerable list) continue;
            foreach (var f in list)
            {
                var name = (string)f.GetType().GetProperty("Name")!.GetValue(f)!;
                if (name.Contains("location") || name.EndsWith("list_start") || name.EndsWith("list_end") || name.EndsWith("_len")) found.Add(name);
            }
        }
        Assert.NotEmpty(found);
        Assert.All(found, name => Assert.True(PostgresSameMeaning.IsPosition(name), $"{name} is not ignored"));
    }
}
