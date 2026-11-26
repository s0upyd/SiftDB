using System.Data.Common;
using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// What a saved DynamoDB connection holds (design section 14, F19): how it signs in, and one account and region, as
/// one Postgres connection is one database. DynamoDB has no connection string, so one is made up from the fields
/// (<c>Auth=profile;Region=eu-west-2;Profile=dev</c>) and read back the same way, which is all the store and "Save as…"
/// need. The secret access key is the provider's secret (contract v14), never a field.
/// </summary>
internal static class DynamoConnectionFields
{
    public const string Profile = "profile", Keys = "keys", Chain = "chain";

    private static readonly FieldCondition WithProfile = new("auth", [Profile]);
    private static readonly FieldCondition WithKeys = new("auth", [Keys]);

    public static readonly IReadOnlyList<ConnectionField> All =
    [
        new("auth", "Sign in with", ConnectionFieldKind.Choice, "server", Default: Profile, Choices:
        [
            new(Profile, "AWS profile"),
            new(Keys, "Access keys"),
            new(Chain, "Default credential chain"),
        ], Help: "A profile covers IAM Identity Center (SSO) and the AWS CLI's files. The default chain is what the AWS tools use on their own: environment variables, then the machine's role."),
        new("profile", "Profile", ConnectionFieldKind.Text, "server", Placeholder: "default",
            Help: "A profile in ~/.aws/config or ~/.aws/credentials. An SSO profile needs `aws sso login` first.", EnabledWhen: WithProfile),
        new("accessKeyId", "Access key ID", ConnectionFieldKind.Text, "server", Required: true, Placeholder: "AKIA…", EnabledWhen: WithKeys),
        new("region", "Region", ConnectionFieldKind.Text, "server", Required: true, Placeholder: "eu-west-2"),
        new("endpoint", "Endpoint URL", ConnectionFieldKind.Text, "advanced", Placeholder: "http://localhost:8000",
            Help: "Only for DynamoDB Local or LocalStack. Empty: AWS itself, for the region above."),
    ];

    public static readonly SecretSpec Secret = new("Secret access key", WithKeys,
        "The other half of the access key. Saved encrypted, like a password, and only used with access keys.");

    private static readonly (string Field, string Keyword)[] Keywords =
        [("auth", "Auth"), ("profile", "Profile"), ("accessKeyId", "AccessKeyId"), ("region", "Region"), ("endpoint", "Endpoint")];

    public static string Build(IReadOnlyDictionary<string, string> fields, string? secret)
    {
        var region = fields.GetValueOrDefault("region")?.Trim();
        if (string.IsNullOrEmpty(region)) throw new ArgumentException("Give the region, e.g. eu-west-2.");
        var auth = AuthOf(fields);
        if (auth is not (Profile or Keys or Chain)) throw new ArgumentException($"'{auth}' is not a way to sign in.");
        if (fields.GetValueOrDefault("endpoint") is { Length: > 0 } e && !Uri.TryCreate(e.Trim(), UriKind.Absolute, out _))
            throw new ArgumentException($"'{e}' is not a URL. DynamoDB Local is usually http://localhost:8000.");
        var b = new DbConnectionStringBuilder();
        foreach (var (field, keyword) in Keywords)
        {
            var value = field == "auth" ? auth : fields.GetValueOrDefault(field)?.Trim();
            if (!string.IsNullOrEmpty(value)) b[keyword] = value;
        }
        if (auth == Keys && !string.IsNullOrEmpty(secret)) b["SecretAccessKey"] = secret;
        return b.ConnectionString;
    }

    public static ParsedConnection Parse(string connectionString)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var fields = new Dictionary<string, string>();
        foreach (var (field, keyword) in Keywords)
            if (b.TryGetValue(keyword, out var v) && v?.ToString() is { Length: > 0 } s) fields[field] = s;
        var secret = b.TryGetValue("SecretAccessKey", out var k) ? k?.ToString() : null;
        return new ParsedConnection(fields, string.IsNullOrEmpty(secret) ? null : secret);
    }

    /// <summary>The way to sign in; an empty field is the default, a profile.</summary>
    public static string AuthOf(IReadOnlyDictionary<string, string> fields) =>
        fields.GetValueOrDefault("auth") is { Length: > 0 } a ? a.Trim() : Profile;

    /// <summary>What a built connection string says, for opening one.</summary>
    public sealed record Settings(string Auth, string Region, string? Profile, string? AccessKeyId, string? Secret, string? Endpoint);

    public static Settings Read(string connectionString)
    {
        var p = Parse(connectionString);
        string? F(string key) => p.Fields.GetValueOrDefault(key);
        return new Settings(AuthOf(p.Fields), F("region") ?? throw new ArgumentException("Give the region, e.g. eu-west-2."),
            F("profile"), F("accessKeyId"), p.Password, F("endpoint"));
    }
}
