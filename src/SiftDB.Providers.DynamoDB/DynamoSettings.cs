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
    public const string LearnKey = "learnAttributes", Sample = "sample", ResultsOnly = "results";
    /// <summary>How many items a table is sampled with, the first time it is used.</summary>
    public const int SampleSize = 20;

    public static readonly IReadOnlyList<ProviderSetting> All =
    [
        new(ScanWarningKey, "Warn before a SELECT reads a whole table", Over1000,
        [
            new(Always, "Every time"),
            new(Over1000, "When it has more than 1,000 items (recommended)", "Scanning a small table costs next to nothing, so it isn't worth a question."),
            new(Over100k, "When it has more than 100,000 items"),
            new(Never, "Never"),
        ], "DynamoDB answers a SELECT whose WHERE doesn't fix the partition key with = or IN by scanning: it reads every item, and bills the read capacity for all of them. Item counts are DynamoDB's own, updated about every six hours, so a table filled in the last few hours may read as smaller than it is."),
        new(LearnKey, "Learn attribute names for completion", Sample,
        [
            new(Sample, "Also read a few items of each table I use (recommended)",
                $"The first time a statement names a table, up to {SampleSize} of its items are read in the background, once per connection: about 1 to 3 read units a table."),
            new(ResultsOnly, "Only from results I run", "No extra reads: attributes are learnt from the rows your SELECTs return."),
        ], "DynamoDB only declares a table's keys. Its other attributes are learnt from items, for completion and the tree, and marked as seen rather than declared."),
    ];

    private volatile string _scanWarning = Over1000;

    private volatile bool _sample = true;

    public void Apply(IReadOnlyDictionary<string, string> values)
    {
        _scanWarning = values.GetValueOrDefault(ScanWarningKey) is { } v && All[0].Choices.Any(c => c.Value == v) ? v : Over1000;
        _sample = values.GetValueOrDefault(LearnKey) != ResultsOnly;
    }

    /// <summary>Whether a table may be read a little, the first time it is used, to learn its attributes.</summary>
    public bool Samples => _sample;

    /// <summary>Whether a scan of a table with this many items (null: not known) is worth asking about.</summary>
    public bool WarnsFor(long? items) => _scanWarning switch
    {
        Never => false,
        Always => true,
        Over100k => items is null or > 100_000,
        _ => items is null or > 1_000,
    };
}
