using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>A saved connection's fields to and from an Npgsql connection string (design section 8).</summary>
public class ConnectionFieldsTests
{
    private readonly PostgresProvider _pg = new();

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] kv) => kv.ToDictionary(x => x.Key, x => x.Value);

    [Fact]
    public void Builds_a_connection_string_from_fields_and_password()
    {
        var cs = _pg.BuildConnectionString(Fields(("host", "db.example"), ("port", "6432"), ("database", "shop"),
            ("username", "app"), ("sslMode", "VerifyFull"), ("rootCertificate", @"C:\certs\root.crt")), "s3cret;x");
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("db.example", b.Host);
        Assert.Equal(6432, b.Port);
        Assert.Equal("shop", b.Database);
        Assert.Equal("app", b.Username);
        Assert.Equal("s3cret;x", b.Password); // quoted properly, not split at the ';'
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
        Assert.Equal(@"C:\certs\root.crt", b.RootCertificate);
    }

    [Fact]
    public void Empty_fields_fall_back_to_defaults_and_no_password_means_none()
    {
        var b = new NpgsqlConnectionStringBuilder(_pg.BuildConnectionString(Fields(("database", "d"), ("port", " ")), null));
        Assert.Equal("localhost", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Null(b.Password);
    }

    [Fact]
    public void Extra_keywords_are_kept_but_named_fields_win()
    {
        var b = new NpgsqlConnectionStringBuilder(_pg.BuildConnectionString(
            Fields(("host", "real"), ("database", "d"), ("extra", "Host=ignored;Timeout=30;Application Name=SiftDB")), null));
        Assert.Equal("real", b.Host);
        Assert.Equal(30, b.Timeout);
        Assert.Equal("SiftDB", b.ApplicationName);
    }

    [Theory]
    [InlineData("port", "abc")]
    [InlineData("port", "70000")]
    [InlineData("sslMode", "Sometimes")]
    public void Rejects_values_that_cannot_work(string key, string value) =>
        Assert.Throws<ArgumentException>(() => _pg.BuildConnectionString(Fields(("database", "d"), (key, value)), null));

    [Fact]
    public void Parses_a_pasted_string_into_fields_with_synonyms_and_extras()
    {
        var parsed = _pg.ParseConnectionString("Server=h;Port=5433;DB=d;User Id=u;PWD=p;SSL Mode=Require;Timeout=30;Options=-c search_path=a");
        Assert.Equal("h", parsed.Fields["host"]);
        Assert.Equal("5433", parsed.Fields["port"]);
        Assert.Equal("d", parsed.Fields["database"]);
        Assert.Equal("u", parsed.Fields["username"]);
        Assert.Equal("Require", parsed.Fields["sslMode"]);
        Assert.Equal("p", parsed.Password);
        Assert.DoesNotContain("p", parsed.Fields.Values); // the password is never a field
        var extra = new NpgsqlConnectionStringBuilder(parsed.Fields["extra"]);
        Assert.Equal(30, extra.Timeout);
        Assert.Equal("-c search_path=a", extra.Options);
        Assert.DoesNotContain("Password", parsed.Fields["extra"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Server", parsed.Fields["extra"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_then_build_gives_the_same_connection()
    {
        const string original = "Host=h;Port=5433;Database=d;Username=u;Password=p;SSL Mode=Require;Timeout=30";
        var parsed = _pg.ParseConnectionString(original);
        var rebuilt = new NpgsqlConnectionStringBuilder(_pg.BuildConnectionString(parsed.Fields, parsed.Password));
        Assert.True(new NpgsqlConnectionStringBuilder(original).EquivalentTo(rebuilt), rebuilt.ConnectionString);
    }

    [Fact]
    public void Only_keywords_actually_given_become_fields()
    {
        var parsed = _pg.ParseConnectionString("Host=h;Database=d");
        Assert.False(parsed.Fields.ContainsKey("port"));
        Assert.False(parsed.Fields.ContainsKey("sslMode"));
        Assert.False(parsed.Fields.ContainsKey("extra"));
        Assert.Null(parsed.Password);
    }

    [Fact]
    public void Read_only_goes_into_the_startup_options_after_any_typed_ones()
    {
        var cs = PostgresConnectionFields.WithOptions("Host=h;Options=-c search_path=a -c default_transaction_read_only=off", new ConnectionOptions(ReadOnly: true));
        var options = new NpgsqlConnectionStringBuilder(cs).Options!;
        Assert.EndsWith("-c default_transaction_read_only=on", options); // the last -c for a setting wins
        Assert.Equal("Host=h", PostgresConnectionFields.WithOptions("Host=h", ConnectionOptions.Default));
    }

    [Fact]
    public void Every_field_has_a_stable_key_and_the_ca_file_depends_on_verification()
    {
        var fields = _pg.ConnectionFields;
        Assert.Equal(["host", "port", "database", "username", "sslMode", "rootCertificate", "extra"], fields.Select(f => f.Key));
        var ca = fields.Single(f => f.Key == "rootCertificate");
        Assert.Equal("sslMode", ca.EnabledWhen!.Key);
        Assert.Equal(["VerifyCA", "VerifyFull"], ca.EnabledWhen.Values);
        Assert.All(fields.Single(f => f.Key == "sslMode").Choices!, c => Assert.True(Enum.TryParse<SslMode>(c.Value, out _), c.Value));
    }
}
