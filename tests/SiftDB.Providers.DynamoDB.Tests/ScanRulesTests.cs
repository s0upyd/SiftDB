namespace SiftDB.Providers.DynamoDB.Tests;

/// <summary>
/// When DynamoDB answers a SELECT with a full scan (design section 14, F19 step 4): unless its WHERE fixes the
/// partition key with = or IN (AWS's rule). Here the partition key is pk.
/// </summary>
public class ScanRulesTests
{
    private readonly PartiqlParser _p = new();

    private bool Scans(string sql)
    {
        var t = ScanRules.SelectTarget(sql, _p) ?? throw new InvalidOperationException("not a select");
        return !ScanRules.FixesPartitionKey(t, "pk");
    }

    [Theory]
    [InlineData("select * from t where pk = 'a'")]
    [InlineData("select * from t where 'a' = pk")]
    [InlineData("select * from t where \"pk\" = 42")]
    [InlineData("select * from t where pk = ?")]
    [InlineData("select * from t where pk IN ['a', 'b']")]
    [InlineData("select * from t where pk in ('a', 'b')")]
    [InlineData("select * from t where pk = 'a' and sk > 3")]
    [InlineData("select * from t where status = 'open' and pk = 'a'")]
    [InlineData("select * from t where pk = 'a' or pk = 'b'")]
    [InlineData("select * from t where (pk = 'a' and sk = 1) or (pk = 'b' and sk = 2)")]
    [InlineData("select * from t where (pk = 'a')")]
    [InlineData("select * from t where pk = 'a' and sk between 1 and 5")]
    [InlineData("select * from t where pk = 'a' order by sk desc")]
    [InlineData("SELECT a, b FROM \"t\" -- note\n WHERE pk = 'a'")]
    public void Fixing_the_partition_key_is_a_query(string sql) => Assert.False(Scans(sql));

    [Theory]
    [InlineData("select * from t")]
    [InlineData("select * from t where status = 'open'")]
    [InlineData("select * from t where sk = 1")]
    [InlineData("select * from t where pk = 'a' or status = 'open'")] // one branch scans, so the whole does
    [InlineData("select * from t where pk > 'a'")]
    [InlineData("select * from t where pk <> 'a'")]
    [InlineData("select * from t where begins_with(pk, 'a')")]
    [InlineData("select * from t where not pk = 'a'")]
    [InlineData("select * from t where pk = other")]                 // compared with another attribute, not a value
    [InlineData("select * from t where total in [500, 600]")]        // IN on another attribute (AWS's own example)
    [InlineData("select * from t where (pk = 'a' or status = 'x') and sk = 1")]
    public void Anything_else_is_a_scan(string sql) => Assert.True(Scans(sql));

    [Fact]
    public void The_table_and_index_are_read_from_the_statement()
    {
        var t = ScanRules.SelectTarget("select * from \"Orders\".\"ByStatus\" where status = 'open'", _p)!;
        Assert.Equal("Orders", t.Table);
        Assert.Equal("ByStatus", t.Index);
        var plain = ScanRules.SelectTarget("select * from orders_2026", _p)!;
        Assert.Equal("orders_2026", plain.Table);
        Assert.Null(plain.Index);
    }

    [Theory]
    [InlineData("update t set a = 1 where pk = 'x'")]
    [InlineData("delete from t where pk = 'x'")]
    [InlineData("insert into t value {'pk': 'x'}")]
    public void Writes_are_not_selects(string sql) => Assert.Null(ScanRules.SelectTarget(sql, _p));

    [Theory]
    [InlineData("always", 3L, true)]
    [InlineData("over1000", 3L, false)]
    [InlineData("over1000", 5000L, true)]
    [InlineData("over1000", null, true)]   // not known: ask
    [InlineData("over100k", 5000L, false)]
    [InlineData("never", 10_000_000L, false)]
    public void The_setting_decides_when_a_scan_is_worth_asking_about(string setting, long? items, bool warns)
    {
        var s = new DynamoSettings();
        s.Apply(new Dictionary<string, string> { [DynamoSettings.ScanWarningKey] = setting });
        Assert.Equal(warns, s.WarnsFor(items));
    }

    [Fact]
    public void An_unknown_setting_value_is_the_default()
    {
        var s = new DynamoSettings();
        s.Apply(new Dictionary<string, string> { [DynamoSettings.ScanWarningKey] = "sometimes" });
        Assert.False(s.WarnsFor(500));
        Assert.True(s.WarnsFor(5000));
    }
}
