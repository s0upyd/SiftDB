using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Expand * (design section 14, F10): what it offers, the text it writes, and where it stays quiet.</summary>
public class StarExpanderTests
{
    private static readonly PostgresProvider Pg = new();

    private static RelationInfo Table(string name, params string[] columns) =>
        new(name, RelationKind.Table, columns.Select(c => new ColumnInfo(c, "integer", true, null)).ToList(), [], [], []);

    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [
            Table("Order", "Id", "CustomerId", "Total"),
            Table("customer", "id", "name", "select"),
            Table("wide", Enumerable.Range(1, 30).Select(i => $"column_number_{i}").ToArray()),
        ]),
    ]);

    private static IReadOnlyList<CodeAction> Find(string sql, FormatOptions? o = null) =>
        StarExpander.Find(Schema, StatementSplitter.For(Pg.Parser), Pg.Identifiers, sql, o ?? FormatOptions.Default);

    private static string Apply(string sql, CodeAction a) => sql.Remove(a.Start, a.Length).Insert(a.Start, a.Text);

    [Theory]
    [InlineData("select * from \"Order\"", "select \"Id\", \"CustomerId\", \"Total\" from \"Order\"")]
    [InlineData("select * from customer", "select id, name, \"select\" from customer")]                     // a reserved word is quoted
    [InlineData("select distinct * from customer c", "select distinct id, name, \"select\" from customer c")]
    [InlineData("select c.* from customer c", "select c.id, c.name, c.\"select\" from customer c")]
    [InlineData("select x, * from customer where id = 1", "select x, id, name, \"select\" from customer where id = 1")]
    [InlineData("select * from \"Order\" o join customer c on c.id = o.\"CustomerId\"",
        "select o.\"Id\", o.\"CustomerId\", o.\"Total\", c.id, c.name, c.\"select\" from \"Order\" o join customer c on c.id = o.\"CustomerId\"")]
    [InlineData("select o.* from \"Order\" o, customer", "select o.\"Id\", o.\"CustomerId\", o.\"Total\" from \"Order\" o, customer")]
    [InlineData("select * from \"Order\", customer", // 112 characters on one line, past the width: one per line
        "select\n    \"Order\".\"Id\",\n    \"Order\".\"CustomerId\",\n    \"Order\".\"Total\",\n    customer.id,\n    customer.name,\n    customer.\"select\" from \"Order\", customer")]
    [InlineData("select * from public.customer", "select id, name, \"select\" from public.customer")]
    public void Expands(string sql, string expected)
    {
        var a = Assert.Single(Find(sql));
        Assert.Equal(expected, Apply(sql, a));
        Assert.True(Pg.SameMeaning(expected, expected)); // valid SQL
        Assert.Null(Pg.Parser.Validate(expected));
    }

    [Fact]
    public void Each_star_gets_its_own_fix()
    {
        var sql = "select o.*, c.* from \"Order\" o join customer c on true";
        var actions = Find(sql);
        Assert.Equal(2, actions.Count);
        Assert.Equal("Expand o.* to the 3 columns of o", actions[0].Title);
        Assert.Equal("Expand c.* to the 3 columns of c", actions[1].Title);
    }

    [Fact]
    public void A_long_list_goes_one_column_per_line()
    {
        var sql = "select * from wide";
        var expanded = Apply(sql, Assert.Single(Find(sql)));
        Assert.StartsWith("select\n    column_number_1,\n    column_number_2,\n", expanded);
        Assert.EndsWith("    column_number_30 from wide", expanded);
        var leading = Apply(sql, Assert.Single(Find(sql, new FormatOptions(Commas: CommaPlacement.Start, Indent: 2))));
        Assert.StartsWith("select\n  column_number_1\n  , column_number_2\n", leading);
    }

    [Theory]
    [InlineData("select count(*) from customer")]                                          // an argument
    [InlineData("select id * 2 from customer")]                                            // multiplication
    [InlineData("select * from nowhere")]                                                  // not in the schema
    [InlineData("select * from customer join \"Order\" using (id)")]                        // USING shows id once
    [InlineData("select * from customer natural join \"Order\"")]
    [InlineData("select * from (select 1 as a) x")]                                       // a derived table
    [InlineData("select * from customer where id in (select \"CustomerId\" from \"Order\")")] // a subquery's table is not in scope
    [InlineData("with x as (select 1) select * from customer")]
    [InlineData("select * from customer union select * from customer")]
    [InlineData("select * from generate_series(1, 3) g")]                                  // a function
    [InlineData("select * from customer as c(a, b, d)")]                                  // renamed columns
    [InlineData("insert into customer select * from customer")]                           // not a plain SELECT
    [InlineData("select public.customer.* from public.customer")]                         // three-part: left alone
    [InlineData("select x.* from customer c")]                                            // no such alias
    public void Not_offered(string sql) => Assert.Empty(Find(sql));
}
