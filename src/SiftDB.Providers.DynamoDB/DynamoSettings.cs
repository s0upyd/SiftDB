using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// DynamoDB's own settings (design section 10), drawn as a category of the settings editor: when the scan warning
/// (F19 step 4) asks before a SELECT that reads a whole table. Applied whenever the settings are saved, so open sheets
/// follow from their next run.
/// </summary>
internal sealed class DynamoSettings
{
    public const string ScanWarningKey = "scanWarning";
    public const string Always = "always", Over1000 = "over1000", Over100k = "over100k", Never = "never";

    public static readonly IReadOnlyList<ProviderSetting> All =
    [
        new(ScanWarningKey, "Warn before a SELECT reads a whole table", Over1000,
        [
            new(Always, "Every time"),
            new(Over1000, "When it has more than 1,000 items (recommended)", "Scanning a small table costs next to nothing, so it isn't worth a question."),
            new(Over100k, "When it has more than 100,000 items"),
            new(Never, "Never"),
        ], "DynamoDB answers a SELECT whose WHERE doesn't fix the partition key with = or IN by scanning: it reads every item, and bills the read capacity for all of them. Item counts are DynamoDB's own, updated about every six hours, so a table filled in the last few hours may read as smaller than it is."),
    ];

    private volatile string _scanWarning = Over1000;

    public void Apply(IReadOnlyDictionary<string, string> values) =>
        _scanWarning = values.GetValueOrDefault(ScanWarningKey) is { } v && All[0].Choices.Any(c => c.Value == v) ? v : Over1000;

    /// <summary>Whether a scan of a table with this many items (null: not known) is worth asking about.</summary>
    public bool WarnsFor(long? items) => _scanWarning switch
    {
        Never => false,
        Always => true,
        Over100k => items is null or > 100_000,
        _ => items is null or > 1_000,
    };
}
