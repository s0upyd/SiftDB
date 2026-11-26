using System.Collections.Concurrent;
using Amazon.DynamoDBv2.Model;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// What SiftDB has learnt about one account and region (one DynamoDB "database"), shared by every sheet on it: the
/// attributes seen in each table's items (DynamoDB only declares the keys), which tables have been sampled, and the table
/// descriptions of the last schema load, so the reload after learning something needs no calls (design section 14, F19).
/// </summary>
internal sealed class DynamoAccount
{
    public AttributeLearning Learning { get; } = new();

    /// <summary>The last schema load: the table names in order, and each one's description.</summary>
    public (List<string> Names, ConcurrentDictionary<string, TableDescription> Tables)? Loaded { get; set; }

    /// <summary>Until when a schema load may be answered from <see cref="Loaded"/>: just after something was learnt.</summary>
    public DateTime ReuseUntil { get; set; }
}

/// <summary>Attribute names (and their types) seen in a table's items, from results and samples.</summary>
internal sealed class AttributeLearning
{
    private sealed class Seen(int order)
    {
        public readonly int Order = order;
        public int Items;
        public readonly Dictionary<string, int> Types = new(StringComparer.Ordinal);
    }

    private sealed class Table
    {
        public int Items;
        public readonly Dictionary<string, Seen> Attributes = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Table> _tables = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sampled = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Adds what these items show. True when a name not seen before turned up (so the schema has grown).</summary>
    public bool Learn(string table, IEnumerable<Dictionary<string, AttributeValue>> items)
    {
        var grew = false;
        lock (_gate)
        {
            if (!_tables.TryGetValue(table, out var t)) _tables[table] = t = new Table();
            foreach (var item in items)
            {
                t.Items++;
                foreach (var (name, value) in item)
                {
                    if (!t.Attributes.TryGetValue(name, out var seen))
                    {
                        t.Attributes[name] = seen = new Seen(t.Attributes.Count);
                        grew = true;
                    }
                    seen.Items++;
                    var type = AttributeText.TypeOf(value);
                    seen.Types[type] = seen.Types.GetValueOrDefault(type) + 1;
                }
            }
        }
        return grew;
    }

    /// <summary>
    /// A table's attributes as seen: the most common first (then in the order first seen), each with the type it mostly
    /// has, how many items had it, and how many items were seen in all.
    /// </summary>
    public IReadOnlyList<(string Name, string Type, int SeenIn, int Of)> Attributes(string table)
    {
        lock (_gate)
        {
            if (!_tables.TryGetValue(table, out var t)) return [];
            return t.Attributes.OrderByDescending(a => a.Value.Items).ThenBy(a => a.Value.Order)
                .Select(a => (a.Key, a.Value.Types.Where(x => x.Key != "NULL").OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault() ?? "NULL",
                    a.Value.Items, t.Items))
                .ToList();
        }
    }

    /// <summary>True the first time it is asked for a table: that caller samples it, no one else does.</summary>
    public bool ClaimSample(string table) => _sampled.TryAdd(table, 0);
}
