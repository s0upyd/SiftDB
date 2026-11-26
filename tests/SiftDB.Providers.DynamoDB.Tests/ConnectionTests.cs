using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB.Tests;

/// <summary>DynamoDB's connection fields, its made-up connection string, and its secret (design section 14, F19).</summary>
[Collection("AWS profile files")] // one test points the SDK at profile files of its own
public class ConnectionTests
{
    private readonly DynamoProvider _p = new();

    [Fact]
    public void A_profile_connection_round_trips_and_carries_no_secret()
    {
        var fields = new Dictionary<string, string> { ["auth"] = "profile", ["profile"] = "dev", ["region"] = "eu-west-2" };
        var cs = _p.BuildConnectionString(fields, "ignored");
        Assert.DoesNotContain("ignored", cs); // a profile has no secret: one sent anyway goes nowhere
        var back = _p.ParseConnectionString(cs);
        Assert.Equal(fields, back.Fields);
        Assert.Null(back.Password);
    }

    [Fact]
    public void Access_keys_carry_the_secret()
    {
        var fields = new Dictionary<string, string> { ["auth"] = "keys", ["accessKeyId"] = "AKIAEXAMPLE", ["region"] = "us-east-1", ["endpoint"] = "http://localhost:8000" };
        var back = _p.ParseConnectionString(_p.BuildConnectionString(fields, "s3cr;et=x"));
        Assert.Equal(fields, back.Fields);
        Assert.Equal("s3cr;et=x", back.Password); // separators inside a value survive
    }

    [Fact]
    public void An_empty_auth_is_a_profile()
    {
        var back = _p.ParseConnectionString(_p.BuildConnectionString(new Dictionary<string, string> { ["region"] = "eu-west-1" }, null));
        Assert.Equal("profile", back.Fields["auth"]);
    }

    [Theory]
    [InlineData("", "Give the region")]
    [InlineData("eu-west-2|endpoint=not a url", "not a URL")]
    public void Bad_fields_are_reported_on_saving(string spec, string message)
    {
        var parts = spec.Split('|');
        var fields = new Dictionary<string, string> { ["region"] = parts[0] };
        if (parts.Length > 1) fields["endpoint"] = parts[1]["endpoint=".Length..];
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => _p.BuildConnectionString(fields, null)).Message);
    }

    [Fact]
    public void The_secret_is_the_secret_access_key_and_only_with_access_keys()
    {
        var s = Assert.IsType<SecretSpec>(_p.Secret);
        Assert.Equal("Secret access key", s.Label);
        Assert.Equal("auth", s.NeededWhen!.Key);
        Assert.Equal(["keys"], s.NeededWhen.Values);
        Assert.Equal("profile", _p.ConnectionFields.Single(f => f.Key == "auth").Default);
    }

    [Fact]
    public async Task Access_keys_without_the_secret_ask_for_it_before_anything_is_sent()
    {
        // No endpoint is listening on this port: the refusal must come first, without trying.
        var cs = _p.BuildConnectionString(new Dictionary<string, string>
            { ["auth"] = "keys", ["accessKeyId"] = "AKIAEXAMPLE", ["region"] = "eu-west-2", ["endpoint"] = "http://127.0.0.1:9" }, null);
        var ex = await Assert.ThrowsAsync<ConnectionAuthenticationException>(() => _p.OpenAsync(cs, ConnectionOptions.Default, CancellationToken.None));
        Assert.Contains("secret access key", ex.Message);
    }

    [Fact]
    public async Task A_missing_profile_says_so()
    {
        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", Path.Combine(Path.GetTempPath(), "siftdb-no-such-aws-config"));
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", Path.Combine(Path.GetTempPath(), "siftdb-no-such-aws-credentials"));
        var cs = _p.BuildConnectionString(new Dictionary<string, string> { ["auth"] = "profile", ["profile"] = "siftdb-nope", ["region"] = "eu-west-2" }, null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _p.OpenAsync(cs, ConnectionOptions.Default, CancellationToken.None));
        Assert.Contains("no AWS profile 'siftdb-nope'", ex.Message);
    }

    [Theory]
    [InlineData("select * from t", null)]
    [InlineData("SELECT a FROM t WHERE b = 1", null)]
    [InlineData("update t set a = 1 where pk = 'x'", "UPDATE")]
    [InlineData("select * from t; delete from t where pk = 'x'", "DELETE")]
    [InlineData("insert into t value {'pk': 'x'}", "INSERT")]
    public void Read_only_runs_only_select(string sql, string? refused)
    {
        var reason = _p.CheckReadOnly(sql);
        if (refused is null) Assert.Null(reason);
        else Assert.Contains($"not {refused}", reason);
    }

    [Fact]
    public void Numbers_and_booleans_are_typed_for_styling()
    {
        Assert.Equal(GenericType.Decimal, _p.GenericTypeOf("N"));
        Assert.Equal(GenericType.Boolean, _p.GenericTypeOf("BOOL"));
        Assert.Equal(GenericType.Text, _p.GenericTypeOf("S"));
        Assert.Equal(GenericType.Text, _p.GenericTypeOf("M"));
    }

    [Fact]
    public void Schemas_are_never_written_and_there_is_no_limit_clause()
    {
        IDatabaseProvider p = _p;
        Assert.False(p.WritesSchemaNames);
        Assert.Null(p.RowLimitClause(100));
    }
}
