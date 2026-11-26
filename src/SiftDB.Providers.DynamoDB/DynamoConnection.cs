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
/// one ExecuteStatement call and commits on its own, unless a transaction is open here, which holds the writes until
/// Commit sends them all in one ExecuteTransaction. Besides the signed-in client it keeps what it has learnt about the
/// tables (their keys here; their other attributes in the <see cref="DynamoAccount"/> every sheet on it shares).
/// </summary>
internal sealed class DynamoConnection : IProviderConnection
{
    private readonly AmazonDynamoDBClient _client;
    private readonly string _region;
    private readonly PartiqlParser _parser;
    private readonly DynamoSettings _settings;
    private readonly DynamoAccount _account;
    private readonly DynamoIdentifiers _names = new();
    private readonly QueuedTransaction _transaction;
    /// <summary>Each table's partition and sort key, from the schema load or asked once; for putting them first.</summary>
    private readonly ConcurrentDictionary<string, string[]> _keys = new(StringComparer.Ordinal);

    private DynamoConnection(AmazonDynamoDBClient client, string region, PartiqlParser parser, DynamoSettings settings, DynamoAccount account)
    {
        _client = client;
        _region = region;
        _parser = parser;
        _settings = settings;
        _account = account;
        _transaction = new QueuedTransaction(client);
    }

    public ITransactionMonitor Transactions => _transaction;

    /// <summary>
    /// Signs in the way the connection says, then proves it with a one-table ListTables, so a bad key or an expired SSO
    /// session is reported on connecting, not at the first statement.
    /// </summary>
    public static async Task<DynamoConnection> OpenAsync(string connectionString, PartiqlParser parser, DynamoSettings settings,
        Func<DynamoConnectionFields.Settings, DynamoAccount> account, CancellationToken ct)
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
        return new DynamoConnection(client, s.Region, parser, settings, account(s));
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

    // ---- running statements ------------------------------------------------------------------------------------

    public async Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct)
    {
        var statement = sql.Trim().TrimEnd(';').TrimEnd();
        var verb = _parser.Verb(statement);

        // In a transaction, writes (and EXISTS conditions) wait for Commit: nothing is sent now.
        if (_transaction.IsOpen && verb is "insert" or "update" or "delete" or "exists")
        {
            var position = _transaction.Queue(statement);
            return new DynamoReader([], false, [], null, (_, _) => throw new InvalidOperationException("Nothing to read."),
                headline: "Queued", detail: $"statement {position} of the transaction: it runs when you commit");
        }

        var (items, next, units) = await PageAsync(statement, null, ct);
        // A filtered scan can return empty pages before it finds anything: read on to the first item (or the end), so
        // the columns can be worked out. Cancelling stops it.
        while (items.Count == 0 && next is not null)
        {
            double? more;
            (items, next, more) = await PageAsync(statement, next, ct);
            if (more is { } u) units = (units ?? 0) + u;
        }

        var table = verb == "select" ? TableOf(statement) : null;
        // What the rows show is learnt for free (no extra reads): their attributes, for completion and the tree.
        if (table is not null && items.Count > 0) Learn(table, items);

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
            names = table is not null ? [.. await KeysOfAsync(table, ct)] : [];
            other = false;
        }
        else
        {
            names = table is not null ? [.. await KeysOfAsync(table, ct)] : [];
            var seen = names.ToHashSet(StringComparer.Ordinal);
            foreach (var item in items)
                foreach (var name in item.Keys)
                    if (seen.Add(name)) names.Add(name);
            other = next is not null; // only a later page can bring an attribute this one didn't have
        }
        // A SELECT inside a transaction runs straight away and sees what is committed, not the writes queued for Commit.
        var aside = verb == "select" && _transaction.Queued > 0 ? "outside the transaction: committed data only" : null;
        return new DynamoReader(names, other, items, next, (token, c) => PageAsync(statement, token, c),
            units, verb == "select" ? "read" : "write", detail: aside);
    }

    /// <summary>One page, with the capacity it used (null when not reported: DynamoDB Local never does).</summary>
    private async Task<(List<Dictionary<string, AttributeValue>>, string?, double?)> PageAsync(string statement, string? token, CancellationToken ct)
    {
        var r = await _client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = statement, NextToken = token, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        }, ct);
        return (r.Items ?? [], string.IsNullOrEmpty(r.NextToken) ? null : r.NextToken, r.ConsumedCapacity?.CapacityUnits);
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

    /// <summary>
    /// The tables a statement names: after FROM (a SELECT or DELETE), UPDATE, or INSERT INTO; an index's table is its
    /// table (<c>from "Orders"."ByStatus"</c> is Orders).
    /// </summary>
    private List<string> TablesOf(string statement)
    {
        var tokens = _parser.Tokenize(statement).Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        var tables = new List<string>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Keyword) continue;
            var w = Word(statement, tokens[i]);
            var name = w is "from" or "update" || (w == "into" && i > 0 && Word(statement, tokens[i - 1]) == "insert") ? tokens[i + 1] : (SqlToken?)null;
            if (name is { Kind: SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier } n) tables.Add(Unquote(statement, n));
        }
        return tables;
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

    // ---- learning attributes (contract v17) -------------------------------------------------------------------

    public event Action? SchemaChanged;

    /// <summary>
    /// The statement at the cursor (a hint from the sheet analysis): a table it names that is known and not sampled yet
    /// gets a few of its items read in the background, once per account, so completion knows its attributes before the
    /// first run. Off with the "only from results" setting. Returns at once.
    /// </summary>
    public void StatementInView(string sql)
    {
        if (!_settings.Samples) return;
        foreach (var table in TablesOf(sql))
        {
            if (!_keys.ContainsKey(table) || !_account.Learning.ClaimSample(table)) continue; // unknown (a typo), or done
            _ = Task.Run(async () =>
            {
                try
                {
                    var r = await _client.ExecuteStatementAsync(new ExecuteStatementRequest
                        { Statement = $"select * from {_names.Quote(table)}", Limit = DynamoSettings.SampleSize });
                    if (r.Items is { Count: > 0 } items) Learn(table, items);
                }
                catch (Exception) { /* only a hint: completion simply knows less */ }
            });
        }
    }

    /// <summary>Adds what these items show; when a new attribute turned up, the schema is loaded again (from the cache).</summary>
    private void Learn(string table, List<Dictionary<string, AttributeValue>> items)
    {
        if (!_account.Learning.Learn(table, items)) return;
        _account.ReuseUntil = DateTime.UtcNow.AddSeconds(10); // the reload this causes needs no calls
        SchemaChanged?.Invoke();
    }

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
    /// Every table, as one schema named after the region: ListTables, then DescribeTable for each (a few at a time).
    /// A table's columns are its keys (its own, then its indexes'), then the attributes learnt from its items. The
    /// reload just after something was learnt is answered from the last load, with no calls.
    /// </summary>
    public async Task<DatabaseSchema> IntrospectAsync(CancellationToken ct)
    {
        if (_account.Loaded is { } kept && DateTime.UtcNow < _account.ReuseUntil)
            return Schema(kept.Names, kept.Tables);

        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await _client.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start }, ct);
            names.AddRange(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        } while (!string.IsNullOrEmpty(start));

        var tables = new ConcurrentDictionary<string, TableDescription>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(names.Select(async name =>
        {
            await gate.WaitAsync(ct);
            try { tables[name] = (await _client.DescribeTableAsync(name, ct)).Table; }
            catch (ResourceNotFoundException) { /* dropped since it was listed */ }
            finally { gate.Release(); }
        }));
        _account.Loaded = (names, tables);
        return Schema(names, tables);
    }

    private DatabaseSchema Schema(List<string> names, ConcurrentDictionary<string, TableDescription> tables) =>
        new([new SchemaInfo(_region, names.Where(tables.ContainsKey).Select(n => Relation(tables[n])).ToList())]);

    private RelationInfo Relation(TableDescription d)
    {
        var keys = KeyNames(d.KeySchema);
        _keys[d.TableName] = keys;
        var types = (d.AttributeDefinitions ?? []).ToDictionary(a => a.AttributeName, a => a.AttributeType.Value);
        var indexes = Indexes(d).Select(i => new IndexInfo(i.Name, i.Keys, false)).ToList();
        var declared = keys.Concat(indexes.SelectMany(i => i.Columns)).Distinct().ToList();
        var columns = declared.Select(a => new ColumnInfo(a, types.GetValueOrDefault(a) ?? "S", IsNullable: !keys.Contains(a), null)).ToList();
        // Then what the items have shown, the most common first: seen, not declared, so any item may lack it.
        foreach (var a in _account.Learning.Attributes(d.TableName))
            if (!declared.Contains(a.Name)) columns.Add(new ColumnInfo(a.Name, a.Type, IsNullable: true, null));
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
    /// six hours), its key, its indexes with their keys and what they project, and for each attribute learnt from items
    /// how often it was seen.
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
        var indexKeys = Indexes(d).SelectMany(i => i.Keys).ToHashSet(StringComparer.Ordinal);
        var learnt = _account.Learning.Attributes(d.TableName).ToDictionary(a => a.Name);
        var columns = Relation(d).Columns.Select(c => new ColumnDetail(c.Name,
            keys.Length > 0 && c.Name == keys[0] ? "Partition key"
            : keys.Length > 1 && c.Name == keys[1] ? "Sort key"
            : indexKeys.Contains(c.Name) ? "Index key"
            : learnt.TryGetValue(c.Name, out var a) ? $"Seen in {a.SeenIn} of the {a.Of} items read so far (not declared: DynamoDB only declares keys)"
            : null, null, null)).ToList();
        return new RelationDetail($"Status {d.TableStatus?.Value?.ToLowerInvariant()}; billing {(d.BillingModeSummary?.BillingMode?.Value ?? "PROVISIONED").ToLowerInvariant().Replace('_', ' ')}",
            d.ItemCount, d.TableSizeBytes, columns, [key], [], indexes, [], [], [], null);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A DynamoDB transaction, as SiftDB holds one (design section 14, F19): DynamoDB has no open transaction, only
    /// ExecuteTransaction, which runs up to 100 statements at once, all or none. So after Begin, writes (and EXISTS
    /// conditions) are queued here instead of sent; Commit sends them in one ExecuteTransaction, Rollback drops them.
    /// The tab is amber while any are queued. A commit DynamoDB refuses writes nothing, and says which statement it
    /// refused and why; either way the transaction is over.
    /// </summary>
    private sealed class QueuedTransaction(AmazonDynamoDBClient client) : ITransactionMonitor, IResultNote
    {
        public const int MaxStatements = 100;
        private readonly List<string> _queued = [];
        private string? _lastCommit;

        public bool IsOpen { get; private set; }
        public int Queued => _queued.Count;
        public bool CanDetectWrites => true;

        /// <summary>Adds a statement for Commit; its position in the transaction (1-based).</summary>
        public int Queue(string statement)
        {
            if (_queued.Count >= MaxStatements)
                throw new InvalidOperationException($"A DynamoDB transaction holds at most {MaxStatements} statements. Commit these first, or roll back.");
            _queued.Add(statement);
            return _queued.Count;
        }

        public Task BeginAsync(CancellationToken ct)
        {
            if (IsOpen) throw new InvalidOperationException("A transaction is already open.");
            IsOpen = true;
            _lastCommit = null;
            return Task.CompletedTask;
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (!IsOpen) throw new InvalidOperationException("No transaction is open.");
            var statements = _queued.ToList();
            _queued.Clear();
            IsOpen = false;
            if (statements.Count == 0) { _lastCommit = "Nothing was queued, so nothing was written."; return; }
            try
            {
                var r = await client.ExecuteTransactionAsync(new ExecuteTransactionRequest
                {
                    TransactStatements = statements.Select(s => new ParameterizedStatement { Statement = s }).ToList(),
                    ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
                }, ct);
                var units = r.ConsumedCapacity?.Sum(c => c.CapacityUnits ?? 0) is { } u and > 0 ? u : (double?)null;
                _lastCommit = $"Committed {statements.Count} statement{(statements.Count == 1 ? "" : "s")}" +
                    (Capacity.Describe(units, "write") is { } said ? $", using {said}." : ".");
            }
            catch (TransactionCanceledException ex)
            {
                // One reason per statement, in order; "None" for those that were fine.
                var refused = (ex.CancellationReasons ?? []).Select((reason, i) => (reason, i))
                    .Where(x => x.reason.Code is { } code && code != "None")
                    .Select(x => $"statement {x.i + 1} ({Short(statements.ElementAtOrDefault(x.i))}): {x.reason.Code}{(string.IsNullOrEmpty(x.reason.Message) ? "" : $", {x.reason.Message}")}")
                    .ToList();
                throw new InvalidOperationException("DynamoDB refused the transaction, so nothing was written" +
                    (refused.Count > 0 ? ": " + string.Join("; ", refused) + "." : $". ({ex.Message})"), ex);
            }
        }

        public Task RollbackAsync(CancellationToken ct)
        {
            if (!IsOpen) throw new InvalidOperationException("No transaction is open.");
            _lastCommit = null;
            _queued.Clear();
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(new TransactionSnapshot(
            !IsOpen ? TransactionState.None : _queued.Count > 0 ? TransactionState.Changed : TransactionState.Open, null));

        public string? NoteHeadline => null;
        /// <summary>What the last commit did, for the sheet's message.</summary>
        public string? NoteDetail => _lastCommit;

        private static string Short(string? s) => s is null ? "?" : s.Length <= 60 ? s : s[..57] + "…";
    }
}
