using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Bridge;

namespace SiftDB.Engine;

/// <summary>How the engine talks back to whoever hosts it (WebView2 host, a test, a future TUI).</summary>
public interface IEngineOutput
{
    void SendEvent(string json);
    void SendBatch(int queryId, EncodedBatch batch);
}

/// <summary>Headless engine. Commands arrive as JSON text; results leave through <see cref="IEngineOutput"/>.</summary>
public sealed class EngineHost : IAsyncDisposable
{
    // Web defaults give camelCase properties; enums go out as camelCase strings ("materializedView").
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<string, IDatabaseProvider> _providers = new();
    private readonly IEngineOutput _out;
    private readonly ConcurrentDictionary<int, ConnectionState> _connections = new();
    private readonly ConcurrentDictionary<int, QuerySession> _queries = new();
    private int _nextConnection, _nextQuery;

    public EngineHost(IEnumerable<IDatabaseProvider> providers, IEngineOutput output)
    {
        foreach (var p in providers)
        {
            if (p.ContractVersion != ProviderContract.CurrentVersion)
                throw new InvalidOperationException(
                    $"Provider '{p.Id}' targets contract v{p.ContractVersion}; this app supports v{ProviderContract.CurrentVersion}.");
            _providers[p.Id] = p;
        }
        _out = output;
    }

    public async Task HandleCommandAsync(string commandJson)
    {
        using var doc = JsonDocument.Parse(commandJson);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        try
        {
            switch (type)
            {
                case "connect":
                    await ConnectAsync(id, root.GetProperty("provider").GetString()!,
                        root.GetProperty("connectionString").GetString()!);
                    break;
                case "execute":
                    await ExecuteAsync(id, root.GetProperty("connectionId").GetInt32(),
                        root.GetProperty("sql").GetString()!);
                    break;
                case "analyzeSheet":
                    AnalyzeSheet(id, root.GetProperty("connectionId").GetInt32(), root.GetProperty("text").GetString()!,
                        root.GetProperty("selectionStart").GetInt32(), root.GetProperty("selectionEnd").GetInt32(),
                        RunPreparer.ParseMode(root.TryGetProperty("mode", out var mode) ? mode.GetString() : null));
                    break;
                case "complete":
                    Complete(id, root.GetProperty("connectionId").GetInt32(), root.GetProperty("text").GetString()!,
                        root.GetProperty("offset").GetInt32());
                    break;
                case "introspect":
                    await IntrospectAsync(id, root.GetProperty("connectionId").GetInt32());
                    break;
                case "fetchBatch":
                    await FetchAsync(root.GetProperty("queryId").GetInt32());
                    break;
                case "cancel":
                    if (_queries.TryGetValue(root.GetProperty("queryId").GetInt32(), out var q)) q.Cancel();
                    break;
                default:
                    Error(id, $"Unknown command '{type}'");
                    break;
            }
        }
        catch (Exception ex)
        {
            Error(id, ex.Message);
        }
    }

    private async Task ConnectAsync(int id, string provider, string connectionString)
    {
        if (!_providers.TryGetValue(provider, out var p)) { Error(id, $"No provider '{provider}'"); return; }
        var conn = await p.OpenAsync(connectionString, CancellationToken.None);
        var connId = Interlocked.Increment(ref _nextConnection);
        _connections[connId] = new ConnectionState(p, conn);
        Event(new { type = "connected", replyTo = id, connectionId = connId });
    }

    /// <summary>
    /// Splits a sheet into statements with the connection's dialect and says which one the cursor/selection would run.
    /// Offsets are UTF-16, matching the editor. Cheap enough to call on every pause in typing.
    /// </summary>
    private void AnalyzeSheet(int id, int connectionId, string text, int selectionStart, int selectionEnd, QueryMode mode)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var statements = state.Provider.Parser.Split(text);
        var pick = StatementPicker.Pick(text, statements, selectionStart, selectionEnd);

        // Identifier problems are worked out against the cached schema; with none loaded there is nothing to say.
        var resolved = state.Schema is { } schema
            ? IdentifierResolver.Resolve(schema, state.Provider.Parser, state.Provider.Identifiers, text)
            : ResolverResult.Empty;
        var prepared = pick.Range is { } r ? RunPreparer.Prepare(text, r, resolved, mode) : null;

        Event(new
        {
            type = "sheetAnalysis", replyTo = id,
            statements = statements.Select(s => new { start = s.Start, length = s.Length }),
            pick = new
            {
                start = pick.Range?.Start ?? 0, length = pick.Range?.Length ?? 0, reason = pick.Reason,
                sql = prepared?.Sql, blocked = prepared?.Blocked,
            },
            corrections = resolved.Corrections.Select(c => new { start = c.Start, length = c.Length, text = c.Text, message = c.Message }),
            issues = resolved.Issues.Select(i => new
            {
                start = i.Start, length = i.Length, code = i.Code, severity = i.Severity, message = i.Message, fixText = i.FixText,
            }),
        });
    }

    private async Task IntrospectAsync(int id, int connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var schema = await state.Connection.IntrospectAsync(CancellationToken.None);
        state.Schema = schema; // completions are answered from this cache, never by querying the database
        Event(new { type = "schema", replyTo = id, connectionId, schema });
    }

    /// <summary>Suggestions at a cursor offset. Pure CPU over the cached schema, so it is safe to call on every keystroke.</summary>
    private void Complete(int id, int connectionId, string text, int offset)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var schema = state.Schema;
        var result = schema is null
            ? new CompletionResult(offset, offset, [])
            : CompletionEngine.Complete(schema, state.Provider.Parser, state.Provider.Identifiers, text, offset);
        Event(new { type = "completions", replyTo = id, start = result.Start, end = result.End, items = result.Items });
    }

    private async Task ExecuteAsync(int id, int connectionId, string sql)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        state.Active?.Cancel();

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        var reader = await state.Connection.ExecuteReaderAsync(sql, cts.Token);
        var writer = new RowBatchWriter(reader);
        var queryId = Interlocked.Increment(ref _nextQuery);
        var session = new QuerySession(queryId, reader, writer, cts, sw);
        _queries[queryId] = session;
        state.Active = session;

        Event(new
        {
            type = "columns", replyTo = id, queryId,
            columns = writer.Columns.Select(c => new { name = c.Name, kind = (int)c.Kind }),
        });
        session.Start();
    }

    private async Task FetchAsync(int queryId)
    {
        if (!_queries.TryGetValue(queryId, out var s)) return;
        try
        {
            var r = s.Channel.Reader;
            while (await r.WaitToReadAsync())
            {
                if (r.TryRead(out var batch)) { _out.SendBatch(queryId, batch); return; }
            }
            var b = s.Breakdown;
            Event(new
            {
                type = "complete", queryId, totalRows = s.TotalRows,
                engineFirstBatchMs = s.FirstBatchMs, engineTotalMs = s.TotalMs,
                engineReadMs = b.ReadMs, engineAppendMs = b.AppendMs, engineWriteWaitMs = b.WriteWaitMs,
            });
        }
        catch (Exception ex) when (s.IsCancelled(ex))
        {
            Event(new { type = "cancelled", queryId });
        }
        catch (Exception ex)
        {
            Event(new { type = "error", queryId, message = ex.Message });
        }
    }

    private void Event(object payload) => _out.SendEvent(JsonSerializer.Serialize(payload, Json));
    private void Error(int replyTo, string message) => Event(new { type = "error", replyTo, message });

    public async ValueTask DisposeAsync()
    {
        foreach (var q in _queries.Values) q.Cancel();
        foreach (var c in _connections.Values) await c.Connection.DisposeAsync();
    }

    private sealed class ConnectionState(IDatabaseProvider provider, IProviderConnection connection)
    {
        public IDatabaseProvider Provider { get; } = provider;
        public IProviderConnection Connection { get; } = connection;
        public QuerySession? Active { get; set; }
        public DatabaseSchema? Schema { get; set; }
    }
}
