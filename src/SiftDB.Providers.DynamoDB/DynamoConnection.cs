using System.Collections.Concurrent;
using System.Data.Common;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// One sheet's "session" with DynamoDB (design section 14, F19). There is no session on the server: every statement is
/// one ExecuteStatement call and commits on its own, so this is just a signed-in client and what it has learnt about
/// the tables' keys.
/// </summary>
internal sealed class DynamoConnection : IProviderConnection
{
    private readonly AmazonDynamoDBClient _client;
    private readonly string _region;
    private readonly PartiqlParser _parser;
    private readonly DynamoSettings _settings;
    /// <summary>Each table's partition and sort key, from the schema load or asked once; for putting them first.</summary>
    private readonly ConcurrentDictionary<string, string[]> _keys = new(StringComparer.Ordinal);

    private DynamoConnection(AmazonDynamoDBClient client, string region, PartiqlParser parser, DynamoSettings settings)
    {
        _client = client;
        _region = region;
        _parser = parser;
        _settings = settings;
    }

    public ITransactionMonitor Transactions { get; } = new AutoCommit();

    /// <summary>
    /// Signs in the way the connection says, then proves it with a one-table ListTables, so a bad key or an expired SSO
    /// session is reported on connecting, not at the first statement.
    /// </summary>
    public static async Task<DynamoConnection> OpenAsync(string connectionString, PartiqlParser parser, DynamoSettings settings, CancellationToken ct)
    {
        var s = DynamoConnectionFields.Read(connectionString);
        var config = new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(s.Region) };
        if (s.Endpoint is { Length: > 0 } endpoint)
        {
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = s.Region;
        }
        var client = s.Auth switch
        {
            DynamoConnectionFields.Keys => new AmazonDynamoDBClient(KeyCredentials(s), config),
            DynamoConnectionFields.Profile => new AmazonDynamoDBClient(ProfileCredentials(s.Profile), config),
            _ => new AmazonDynamoDBClient(config), // the SDK's own default chain
        };
        try
        {
            await client.ListTablesAsync(new ListTablesRequest { Limit = 1 }, ct);
        }
        catch (Exception ex) when (SignInRefused(ex) is { } why)
        {
            client.Dispose();
            throw new ConnectionAuthenticationException(why, ex);
        }
        catch (AmazonClientException ex) when (s.Auth == DynamoConnectionFields.Profile && ex.Message.Contains("SSO", StringComparison.OrdinalIgnoreCase))
        {
            // The SDK only says "No valid SSO Token could be found": say what to do about it.
            client.Dispose();
            var name = string.IsNullOrWhiteSpace(s.Profile) ? "default" : s.Profile.Trim();
            throw new InvalidOperationException(
                $"The SSO sign-in for the profile '{name}' has run out, or never happened. Run `aws sso login --profile {name}`, then connect again. ({ex.Message})", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new DynamoConnection(client, s.Region, parser, settings);
    }

    /// <summary>
    /// Access keys: a missing secret is refused here, before anything is sent, so the prompt asks for it (the engine
    /// tries without one first; design section 8).
    /// </summary>
    private static BasicAWSCredentials KeyCredentials(DynamoConnectionFields.Settings s)
    {
        if (string.IsNullOrEmpty(s.AccessKeyId)) throw new ArgumentException("Give the access key ID.");
        if (string.IsNullOrEmpty(s.Secret))
            throw new ConnectionAuthenticationException("The secret access key is needed.", new InvalidOperationException("No secret access key."));
        return new BasicAWSCredentials(s.AccessKeyId, s.Secret);
    }

    private static AWSCredentials ProfileCredentials(string? profile)
    {
        var name = string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim();
        if (new CredentialProfileStoreChain().TryGetAWSCredentials(name, out var credentials)) return credentials;
        throw new InvalidOperationException(
            $"There is no AWS profile '{name}' in ~/.aws/config or ~/.aws/credentials. `aws configure` (or `aws configure sso`) makes one.");
    }

    /// <summary>The reason, when AWS turned the credentials down (rather than anything else going wrong).</summary>
    private static string? SignInRefused(Exception ex) => ex switch
    {
        AmazonServiceException { ErrorCode: "UnrecognizedClientException" or "InvalidSignatureException" or "MissingAuthenticationTokenException" or "InvalidClientTokenId" } e
            => $"AWS turned the credentials down: {e.Message}",
        AmazonServiceException { ErrorCode: "ExpiredTokenException" or "ExpiredToken" } e
            => $"The credentials have expired. For an SSO profile, run `aws sso login`. ({e.Message})",
        _ => null,
    };

    public async Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
    {
        var statement = sql.Trim().TrimEnd(';').TrimEnd();
        var (items, next) = await PageAsync(statement, null, ct);
        // A filtered scan can return empty pages before it finds anything: read on to the first item (or the end), so
        // the columns can be worked out. Cancelling stops it.
        while (items.Count == 0 && next is not null) (items, next) = await PageAsync(statement, next, ct);

        var verb = _parser.Verb(statement);
        List<string> names;
        bool other;
        if (verb == "select" && Projection(statement) is { } named)
        {
            names = named; // DynamoDB returns only those, so there is nothing else to collect
            other = false;
        }
        else if (items.Count == 0)
        {
            // Nothing came back: a write (no columns at all), or an empty SELECT (its table's keys, so the grid has headers).
            names = verb == "select" && TableOf(statement) is { } t ? [.. await KeysOfAsync(t, ct)] : [];
            other = false;
        }
        else
        {
            names = verb == "select" && TableOf(statement) is { } t ? [.. await KeysOfAsync(t, ct)] : [];
            var seen = names.ToHashSet(StringComparer.Ordinal);
            foreach (var item in items)
                foreach (var name in item.Keys)
                    if (seen.Add(name)) names.Add(name);
            other = next is not null; // only a later page can bring an attribute this one didn't have
        }
        return new DynamoReader(names, other, items, next, (token, c) => PageAsync(statement, token, c));
    }

    private async Task<(List<Dictionary<string, AttributeValue>>, string?)> PageAsync(string statement, string? token, CancellationToken ct)
    {
        var r = await _client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, NextToken = token }, ct);
        return (r.Items ?? [], string.IsNullOrEmpty(r.NextToken) ? null : r.NextToken);
    }

    /// <summary>The attributes a SELECT names, when it names only plain attributes (<c>select pk, "Total" from …</c>); else null.</summary>
    private List<string>? Projection(string statement)
    {
        var tokens = _parser.Tokenize(statement).Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        var from = tokens.FindIndex(t => t.Kind == SqlTokenKind.Keyword && Word(statement, t) == "from");
        if (from < 2) return null;
        var names = new List<string>();
        for (var i = 1; i < from; i += 2)
        {
            var t = tokens[i];
            if (t.Kind is not (SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)) return null;
            if (i + 1 < from && !(tokens[i + 1].Kind == SqlTokenKind.Punctuation && statement[tokens[i + 1].Start] == ',')) return null;
            names.Add(Unquote(statement, t));
        }
        return names;
    }

    /// <summary>The table a SELECT reads (<c>from "Orders"</c>, or <c>from "Orders"."ByCustomer"</c> for an index).</summary>
    private string? TableOf(string statement)
    {
        var tokens = _parser.Tokenize(statement).Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        var from = tokens.FindIndex(t => t.Kind == SqlTokenKind.Keyword && Word(statement, t) == "from");
        return from >= 0 && from + 1 < tokens.Count && tokens[from + 1].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier
            ? Unquote(statement, tokens[from + 1]) : null;
    }

    private static string Word(string s, SqlToken t) => s[t.Start..t.End].ToLowerInvariant();

    private static string Unquote(string s, SqlToken t) =>
        t.Kind == SqlTokenKind.QuotedIdentifier ? s[(t.Start + 1)..(t.End - 1)].Replace("\"\"", "\"") : s[t.Start..t.End];

    /// <summary>A table's partition and sort key, from the schema load, else asked once. None when it can't be asked.</summary>
    private async Task<string[]> KeysOfAsync(string table, CancellationToken ct)
    {
        if (_keys.TryGetValue(table, out var keys)) return keys;
        try
        {
            var d = (await _client.DescribeTableAsync(table, ct)).Table;
            return _keys[table] = KeyNames(d.KeySchema);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return []; }
    }

    private static string[] KeyNames(List<KeySchemaElement>? schema) =>
        (schema ?? []).OrderBy(k => k.KeyType == KeyType.HASH ? 0 : 1).Select(k => k.AttributeName).ToArray();

    // ---- the scan warning (F19 step 4) -------------------------------------------------------------------------

    /// <summary>Table descriptions for the scan check, kept a minute: it is asked before every run.</summary>
    private readonly ConcurrentDictionary<string, (TableDescription Table, DateTime At)> _described = new(StringComparer.Ordinal);

    /// <summary>
    /// Each SELECT in <paramref name="sql"/> that DynamoDB would answer with a full scan (<see cref="ScanRules"/>), with
    /// the item count of what it reads, when the scan-warning setting thinks it's worth asking. A table it can't
    /// describe (a typo) gives none: DynamoDB will say so when it runs.
    /// </summary>
    public async Task<IReadOnlyList<FullScan>> FullScansAsync(string sql, CancellationToken ct)
    {
        var scans = new List<FullScan>();
        foreach (var range in _parser.Split(sql))
        {
            if (ScanRules.SelectTarget(sql.Substring(range.Start, range.Length), _parser) is not { } target) continue;
            if (await DescribedAsync(target.Table, ct) is not { } d) continue;
            string[] keys;
            long? items;
            if (target.Index is { } name)
            {
                var gsi = d.GlobalSecondaryIndexes?.FirstOrDefault(i => i.IndexName == name);
                var lsi = d.LocalSecondaryIndexes?.FirstOrDefault(i => i.IndexName == name);
                if (gsi is null && lsi is null) continue; // no such index: DynamoDB will say so
                keys = KeyNames(gsi?.KeySchema ?? lsi!.KeySchema);
                items = gsi?.ItemCount ?? lsi?.ItemCount;
            }
            else
            {
                keys = KeyNames(d.KeySchema);
                items = d.ItemCount;
            }
            if (keys.Length == 0 || ScanRules.FixesPartitionKey(target, keys[0]) || !_settings.WarnsFor(items)) continue;
            scans.Add(new FullScan(target.Table, target.Index, keys[0], items));
        }
        return scans;
    }

    private async Task<TableDescription?> DescribedAsync(string table, CancellationToken ct)
    {
        if (_described.TryGetValue(table, out var kept) && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(1)) return kept.Table;
        try
        {
            var d = (await _client.DescribeTableAsync(table, ct)).Table;
            _described[table] = (d, DateTime.UtcNow);
            return d;
        }
        catch (ResourceNotFoundException) { return null; }
    }

    // ---- the schema ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Every table, as one schema named after the region: ListTables, then DescribeTable for each (a few at a time). The
    /// keys are the only attributes a table declares, so they are its columns: the table's own keys first, then the
    /// indexes' keys. The partition and sort key are the primary key; each index is an index.
    /// </summary>
    public async Task<DatabaseSchema> IntrospectAsync(CancellationToken ct)
    {
        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await _client.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start }, ct);
            names.AddRange(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        } while (!string.IsNullOrEmpty(start));

        var relations = new RelationInfo?[names.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(names.Select(async (name, i) =>
        {
            await gate.WaitAsync(ct);
            try { relations[i] = Relation((await _client.DescribeTableAsync(name, ct)).Table); }
            catch (ResourceNotFoundException) { /* dropped since it was listed */ }
            finally { gate.Release(); }
        }));
        return new DatabaseSchema([new SchemaInfo(_region, relations.Where(r => r is not null).Select(r => r!).ToList())]);
    }

    private RelationInfo Relation(TableDescription d)
    {
        var keys = KeyNames(d.KeySchema);
        _keys[d.TableName] = keys;
        var types = (d.AttributeDefinitions ?? []).ToDictionary(a => a.AttributeName, a => a.AttributeType.Value);
        var indexes = Indexes(d).Select(i => new IndexInfo(i.Name, i.Keys, false)).ToList();
        var columns = keys.Concat(indexes.SelectMany(i => i.Columns)).Distinct()
            .Select(a => new ColumnInfo(a, types.GetValueOrDefault(a) ?? "S", IsNullable: !keys.Contains(a), null)).ToList();
        return new RelationInfo(d.TableName, RelationKind.Table, columns, keys, [], indexes);
    }

    private static IEnumerable<(string Name, string Kind, string[] Keys, string Projection, long? Bytes)> Indexes(TableDescription d) =>
        (d.GlobalSecondaryIndexes ?? []).Select(g => (g.IndexName, "global", KeyNames(g.KeySchema), Projection(g.Projection), g.IndexSizeBytes))
        .Concat((d.LocalSecondaryIndexes ?? []).Select(l => (l.IndexName, "local", KeyNames(l.KeySchema), Projection(l.Projection), l.IndexSizeBytes)));

    private static string Projection(Amazon.DynamoDBv2.Model.Projection? p) => p?.ProjectionType?.Value switch
    {
        "ALL" => "all attributes",
        "KEYS_ONLY" => "keys only",
        "INCLUDE" => "keys and " + string.Join(", ", p.NonKeyAttributes ?? []),
        _ => "",
    };

    /// <summary>
    /// A table's details for the tree (F13): its approximate item count and size (DynamoDB updates them about every
    /// six hours), its key, and its indexes with their keys and what they project.
    /// </summary>
    public async Task<RelationDetail?> DescribeRelationAsync(string schema, string relation, CancellationToken ct)
    {
        TableDescription d;
        try { d = (await _client.DescribeTableAsync(relation, ct)).Table; }
        catch (ResourceNotFoundException) { return null; }
        var keys = KeyNames(d.KeySchema);
        var key = new KeyConstraint("primary key", true, keys,
            string.Join(", ", keys.Select((k, i) => $"{k} ({(i == 0 ? "partition" : "sort")} key)")));
        var indexes = Indexes(d).Select(i => new IndexDetail(i.Name, i.Kind, false, false, string.Join(", ", i.Keys), null, i.Bytes, null,
            $"{i.Kind} secondary index {i.Name} ({string.Join(", ", i.Keys)}), projecting {i.Projection}")).ToList();
        var columns = Relation(d).Columns.Select(c => new ColumnDetail(c.Name,
            keys.Length > 0 && c.Name == keys[0] ? "Partition key" : keys.Length > 1 && c.Name == keys[1] ? "Sort key" : "Index key", null, null)).ToList();
        return new RelationDetail($"Status {d.TableStatus?.Value?.ToLowerInvariant()}; billing {(d.BillingModeSummary?.BillingMode?.Value ?? "PROVISIONED").ToLowerInvariant().Replace('_', ' ')}",
            d.ItemCount, d.TableSizeBytes, columns, [key], [], indexes, [], [], [], null);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// DynamoDB commits every statement on its own: the state is always "no transaction", so the tab never goes amber.
    /// ExecuteTransaction batches come later (design section 14, F19).
    /// </summary>
    private sealed class AutoCommit : ITransactionMonitor
    {
        private const string NoTransactions = "DynamoDB runs every statement on its own and commits it straight away: there is no transaction to begin, commit or roll back.";
        public bool CanDetectWrites => true;
        public Task BeginAsync(CancellationToken ct) => throw new NotSupportedException(NoTransactions);
        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException(NoTransactions);
        public Task RollbackAsync(CancellationToken ct) => throw new NotSupportedException(NoTransactions);
        public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(new TransactionSnapshot(TransactionState.None, null));
    }
}
