using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Statements that change every row of a table (the missing-WHERE warning, design section 14, F1).</summary>
public class WholeTableRulesTests
{
    private static IReadOnlyList<WholeTableChange> Find(string sql) => new PostgresProvider().ChangesEveryRow(sql);

    [Theory]
    [InlineData("update orders set status = 'x'", WholeTableAction.Update, null, "orders")]
    [InlineData("UPDATE Orders SET status = 'x'", WholeTableAction.Update, null, "orders")]            // folded, as the server does
    [InlineData("update \"Orders\" set status = 'x'", WholeTableAction.Update, null, "Orders")]
    [InlineData("update sales.orders o set status = 'x' from customer c", WholeTableAction.Update, "sales", "orders")] // a join is no WHERE
    [InlineData("delete from orders", WholeTableAction.Delete, null, "orders")]
    [InlineData("delete from only orders", WholeTableAction.Delete, null, "orders")]
    [InlineData("delete from orders using customer c", WholeTableAction.Delete, null, "orders")]
    [InlineData("delete -- a comment\n from orders /* where x = 1 */", WholeTableAction.Delete, null, "orders")]
    [InlineData("delete from orders returning *", WholeTableAction.Delete, null, "orders")]
    [InlineData("truncate orders", WholeTableAction.Truncate, null, "orders")]
    [InlineData("truncate table sales.orders restart identity cascade", WholeTableAction.Truncate, "sales", "orders")]
    [InlineData("with gone as (delete from orders returning *) select count(*) from gone", WholeTableAction.Delete, null, "orders")]
    public void Found(string sql, WholeTableAction action, string? schema, string table) =>
        Assert.Equal([new WholeTableChange(action, schema, table)], Find(sql));

    [Theory]
    [InlineData("update orders set status = 'x' where id = 1")]
    [InlineData("update orders set status = 'x' where true")]           // a WHERE written on purpose is the user's choice
    [InlineData("delete from orders where id in (select id from old)")]
    [InlineData("delete from orders where current of c")]
    [InlineData("select * from orders")]
    [InlineData("insert into orders select * from old")]
    [InlineData("delete from orders where")]                             // does not parse: the server reports it
    [InlineData("do $$ begin delete from orders; end $$")]                 // a body is text: not looked into
    [InlineData("")]
    public void Not_found(string sql) => Assert.Empty(Find(sql));

    [Fact]
    public void Every_statement_in_a_run_is_seen()
    {
        Assert.Equal(
            [new(WholeTableAction.Delete, null, "a"), new(WholeTableAction.Truncate, null, "b"), new(WholeTableAction.Truncate, null, "c")],
            Find("delete from a; select 1; update a set x = 1 where y; truncate b, c"));
    }
}
