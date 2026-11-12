namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Statements Postgres refuses inside a transaction block, which auto-transaction has to step out of.</summary>
public class TransactionRulesTests
{
    private static bool Refused(string sql) => new PostgresProvider().CannotRunInTransaction(sql);

    [Theory]
    [InlineData("vacuum t")]
    [InlineData("VACUUM (verbose) t")]
    [InlineData("create database d")]
    [InlineData("drop database d")]
    [InlineData("alter system set work_mem = '64MB'")]
    [InlineData("create index concurrently i on t (x)")]
    [InlineData("drop index concurrently i")]
    [InlineData("reindex (concurrently) table t")]
    [InlineData("reindex table concurrently t")]
    [InlineData("reindex database d")]
    [InlineData("reindex system d")]
    [InlineData("cluster")]
    [InlineData("discard all")]
    [InlineData("create tablespace ts location '/x'")]
    [InlineData("update t set x = 1; vacuum t")] // one such statement is enough
    public void Refused_inside_a_block(string sql) => Assert.True(Refused(sql));

    [Theory]
    [InlineData("select 1")]
    [InlineData("update t set x = 1")]
    [InlineData("create index i on t (x)")]
    [InlineData("drop index i")]
    [InlineData("reindex table t")]
    [InlineData("cluster t using i")]
    [InlineData("discard temp")]
    [InlineData("alter type e add value 'x'")] // allowed in a block since Postgres 12
    [InlineData("begin")]
    [InlineData("commit")]
    [InlineData("call p()")]
    [InlineData("select 'vacuum t'")]
    [InlineData("-- vacuum t\nselect 1")]
    [InlineData("vacuum from where")] // does not parse: the server reports it
    [InlineData("")]
    public void Fine_inside_a_block(string sql) => Assert.False(Refused(sql));
}
