using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// Amazon DynamoDB, through PartiQL (design section 14, F19). Sheets run statements with ExecuteStatement
/// (<c>SELECT * FROM "Orders" WHERE pk = 'c#42'</c>, and INSERT, UPDATE and DELETE), and rows stream back page by page.
/// One saved connection is one account and region. Loaded as a plugin from providers\dynamodb\ (F18), so the AWS SDK
/// lives only there.
/// </summary>
public sealed class DynamoProvider : IDatabaseProvider
{
    private readonly PartiqlParser _parser = new();
    private readonly DynamoSettings _settings = new();

    /// <summary>When the scan warning asks (F19 step 4).</summary>
    public IReadOnlyList<ProviderSetting> Settings => DynamoSettings.All;
    public void ApplySettings(IReadOnlyDictionary<string, string> values) => _settings.Apply(values);

    public string Id => "dynamodb";
    public string DisplayName => "Amazon DynamoDB";
    public int ContractVersion => ProviderContract.CurrentVersion;
    public ISqlParser Parser => _parser;
    public IIdentifierPolicy Identifiers { get; } = new DynamoIdentifiers();
    public IReadOnlyList<ConnectionField> ConnectionFields => DynamoConnectionFields.All;
    /// <summary>The secret access key, needed only when signing in with access keys (contract v14).</summary>
    public SecretSpec? Secret => DynamoConnectionFields.Secret;
    /// <summary>No schemas: the region only groups the tables in the tree, and is never written.</summary>
    public bool WritesSchemaNames => false;
    /// <summary>PartiQL has no LIMIT: "Select top 100 rows" is a plain SELECT, read page by page as the grid shows it.</summary>
    public string? RowLimitClause(int rows) => null;
    /// <summary>Every statement commits on its own: no Begin, no auto-transaction. (ExecuteTransaction batches come later.)</summary>
    public bool HasTransactions => false;

    public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) => DynamoConnectionFields.Build(fields, password);
    public ParsedConnection ParseConnectionString(string connectionString) => DynamoConnectionFields.Parse(connectionString);

    /// <summary>DynamoDB has no read-only sessions: SiftDB refuses anything but a SELECT. Real protection is an IAM policy.</summary>
    public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;

    public string? CheckReadOnly(string sql) => _parser.Split(sql).Select(r => _parser.Verb(sql.Substring(r.Start, r.Length))).FirstOrDefault(v => v != "select") is { } verb
        ? $"This connection is read-only, so SiftDB runs only SELECT on it, not {verb.ToUpperInvariant()}. (DynamoDB has no read-only sessions; an IAM policy is the real protection.)"
        : null;

    public bool CannotRunInTransaction(string sql) => false;

    /// <summary>Numbers (N) and booleans (BOOL) as such, for result styling; everything else is text.</summary>
    public GenericType GenericTypeOf(string dataTypeName) => dataTypeName switch
    {
        "N" => GenericType.Decimal,
        "BOOL" => GenericType.Boolean,
        _ => GenericType.Text,
    };

    public async Task<IProviderConnection> OpenAsync(string connectionString, ConnectionOptions options, CancellationToken ct) =>
        await DynamoConnection.OpenAsync(connectionString, _parser, _settings, ct);
}
