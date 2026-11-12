namespace SiftDB.Providers.Postgres.Tests;

/// <summary>What a read-only connection refuses to send (design section 8), from the parse tree, not the text.</summary>
public class ReadOnlyGuardTests
{
    private static string? Check(string sql) => new PostgresProvider().CheckReadOnly(sql);

    [Theory]
    [InlineData("set default_transaction_read_only = off")]
    [InlineData("SET Default_Transaction_Read_Only TO false")]
    [InlineData("set default_transaction_read_only = 0")]
    [InlineData("set default_transaction_read_only = 'of'")]
    [InlineData("set session default_transaction_read_only = no")]
    [InlineData("set local transaction_read_only = false")]
    [InlineData("set transaction_read_only to off")]
    [InlineData("set session characteristics as transaction read write")]
    [InlineData("set transaction read write")]
    [InlineData("set transaction isolation level serializable, read write")]
    [InlineData("begin read write")]
    [InlineData("begin transaction isolation level repeatable read read write")]
    [InlineData("start transaction read write")]
    [InlineData("select set_config('default_transaction_read_only', 'off', false)")]
    [InlineData("select 1 where set_config('transaction_read_only', 'off', true) is not null")]
    [InlineData("/* sneaky */ SeT   default_transaction_read_only=off -- comment")]
    [InlineData("select 1; set default_transaction_read_only = off; select 2")]
    public void Blocks_switching_read_only_off(string sql) => Assert.NotNull(Check(sql));

    [Theory]
    [InlineData("select * from pg_class")]
    [InlineData("insert into t values (1)")] // the server refuses this itself; the guard only protects the setting
    [InlineData("set default_transaction_read_only = on")]
    [InlineData("set default_transaction_read_only = true")]
    [InlineData("set default_transaction_read_only to default")] // the default is the read-only startup value
    [InlineData("reset default_transaction_read_only")]
    [InlineData("reset all")]
    [InlineData("discard all")]
    [InlineData("begin")]
    [InlineData("begin read only")]
    [InlineData("start transaction isolation level serializable")]
    [InlineData("set transaction read only")]
    [InlineData("set search_path = app, public")]
    [InlineData("select set_config('search_path', 'app', false)")]
    [InlineData("select 'set default_transaction_read_only = off'")] // a string is not a statement
    [InlineData("-- set default_transaction_read_only = off\nselect 1")]
    [InlineData("select from where")] // does not parse: the server reports it
    public void Allows_everything_else(string sql) => Assert.Null(Check(sql));

    [Fact]
    public void Says_what_to_do_instead() =>
        Assert.Contains("writable connection", Check("begin read write"));
}
