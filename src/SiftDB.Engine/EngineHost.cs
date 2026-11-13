using System.Collections.Concurrent;
using System.Data.Common;
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
public sealed partial class EngineHost : IAsyncDisposable
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

    /// <param name="store">Saved connections. Without one, only pasted connection strings can be used.</param>
    /// <param name="session">Autosave and restore of the open sheets. Without one, the session is not kept.</param>
    /// <param name="settings">User settings. Without one, the defaults apply.</param>
    public EngineHost(IEnumerable<IDatabaseProvider> providers, IEngineOutput output, ConnectionStore? store = null, SessionStore? session = null,
        SettingsStore? settings = null)
    {
        _store = store;
        _session = session;
        _settings = settings;
        foreach (var p in providers)
        {
            if (p.ContractVersion != ProviderContract.CurrentVersion)
                throw new InvalidOperationException(
                    $"Provider '{p.Id}' targets contract v{p.ContractVersion}; this app supports v{ProviderContract.CurrentVersion}.");
            _providers[p.Id] = p;
        }
        _out = output;
        ApplyProviderSettings(CurrentSettings);
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
                    if (root.TryGetProperty("savedId", out var savedId))
                        await ConnectSavedAsync(id, savedId.GetString()!, OptionalString(root, "password"));
                    else
                        await ConnectAsync(id, root.GetProperty("provider").GetString()!,
                            root.GetProperty("connectionString").GetString()!, ConnectionOptions.Default, savedId: null);
                    break;
                case "listConnections":
                    ListConnections(id);
                    break;
                case "saveConnection":
                    SaveConnection(id, root);
                    break;
                case "deleteConnection":
                    DeleteConnection(id, root.GetProperty("savedId").GetString()!);
                    break;
                case "testConnection":
                    await TestConnectionAsync(id, root);
                    break;
                case "openFile":
                    OpenFile(id, root.GetProperty("path").GetString()!);
                    break;
                case "saveFile":
                    SaveFile(id, root);
                    break;
                case "saveSession":
                    SaveSession(id, root);
                    break;
                case "loadSession":
                    LoadSession(id);
                    break;
                case "exportBegin":
                    ExportBegin(id, root);
                    break;
                case "exportWrite":
                    ExportWrite(id, root);
                    break;
                case "exportEnd":
                    ExportEnd(id, root);
                    break;
                case "getSettings":
                    GetSettings(id);
                    break;
                case "saveSettings":
                    SaveSettings(id, root);
                    break;
                case "parseConnectionString":
                    ParseConnectionString(id, root.GetProperty("provider").GetString()!, root.GetProperty("connectionString").GetString()!);
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
                case "transaction":
                    await TransactionCommandAsync(id, root.GetProperty("connectionId").GetInt32(), root.GetProperty("action").GetString()!);
                    break;
                case "complete":
                    Complete(id, root.GetProperty("connectionId").GetInt32(), root.GetProperty("text").GetString()!,
                        root.GetProperty("offset").GetInt32());
                    break;
                case "introspect":
                    // "send": false refreshes this session's schema cache (completions, the resolver) without shipping the
                    // whole schema to the UI again; only the sidebar needs a copy.
                    await IntrospectAsync(id, root.GetProperty("connectionId").GetInt32(),
                        send: !root.TryGetProperty("send", out var send) || send.GetBoolean());
                    break;
                case "disconnect":
                    await DisconnectAsync(id, root.GetProperty("connectionId").GetInt32());
                    break;
                case "fetchBatch":
                    await FetchAsync(root.GetProperty("queryId").GetInt32());
                    break;
                case "cancel":
                    // By query id once rows are flowing. By connection when the statement has not produced a result yet (a slow
                    // aggregate, pg_sleep): then there is no query id to name, and that is exactly when cancelling matters.
                    if (root.TryGetProperty("queryId", out var queryIdElement))
                    {
                        if (_queries.TryGetValue(queryIdElement.GetInt32(), out var q)) q.Cancel();
                    }
                    else if (root.TryGetProperty("connectionId", out var cancelConnection) &&
                             _connections.TryGetValue(cancelConnection.GetInt32(), out var cancelState))
                    {
                        CancelCurrent(cancelState);
                    }
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

    /// <summary>Opens a session. <paramref name="savedId"/> is the saved connection it came from, or null for a pasted string.</summary>
    private async Task<bool> ConnectAsync(int id, string provider, string connectionString, ConnectionOptions options, string? savedId,
        bool autoTransaction = false)
    {
        if (!_providers.TryGetValue(provider, out var p)) { Error(id, $"No provider '{provider}'"); return false; }
        var conn = await p.OpenAsync(connectionString, options, CancellationToken.None);
        var connId = Interlocked.Increment(ref _nextConnection);
        // Auto-transaction is for writable sessions: on a read-only one there is nothing to roll back.
        var auto = autoTransaction && !options.ReadOnly;
        var state = new ConnectionState(connId, p, conn) { SavedId = savedId, ReadOnly = options.ReadOnly, AutoTransaction = auto };
        _connections[connId] = state;
        Event(new { type = "connected", replyTo = id, connectionId = connId, savedId, readOnly = options.ReadOnly, autoTransaction = auto });
        await state.SessionLock.WaitAsync();
        try { await ObserveTransactionAsync(state, statement: false, rowsAffected: -1); }
        finally { state.SessionLock.Release(); }
        return true;
    }

    /// <summary>
    /// Splits a sheet into statements with the connection's dialect and says which one the cursor/selection would run.
    /// Offsets are UTF-16, matching the editor. Cheap enough to call on every pause in typing.
    /// </summary>
    private void AnalyzeSheet(int id, int connectionId, string text, int selectionStart, int selectionEnd, QueryMode mode)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var parser = StatementSplitter.For(state.Provider.Parser); // blank lines end statements too
        var statements = parser.Split(text);
        var pick = StatementPicker.Pick(text, statements, selectionStart, selectionEnd);

        // Identifier problems are worked out against the cached schema; with none loaded there is nothing to say.
        var resolved = state.Schema is { } schema
            ? IdentifierResolver.Resolve(schema, parser, state.Provider.Identifiers, text, state.Provider.Dialect, state.Provider.GenericTypeOf)
            : ResolverResult.Empty;

        // Two statements on adjacent lines with no ';' between them read as one, which fails. Say that, rather than the
        // confusing things the resolver finds in the joined text (the same table twice makes every column "ambiguous").
        string? missingSemicolon = null;
        if (pick.Range is { } picked && parser.MissingSemicolon(text, picked) is { } gap)
        {
            var line = text.AsSpan(0, gap.AboveEnd).Count('\n') + 1; // the line the statement above ends on
            missingSemicolon = $"This looks like two statements. Is a ; missing at the end of line {line}?";
            var word = parser.Tokenize(text).First(t => t.Start == gap.NextStart);
            resolved = resolved with
            {
                Issues = [
                    .. resolved.Issues.Where(i => i.Code != IssueCode.Ambiguous || i.Start < picked.Start || i.Start >= picked.End),
                    new Issue(word.Start, word.Length, IssueCode.MissingSemicolon, IssueSeverity.Error, missingSemicolon, null),
                ],
            };
        }
        var prepared = pick.Range is { } r ? RunPreparer.Prepare(text, r, resolved, mode) : null;
        if (prepared is not null && missingSemicolon is not null) prepared = prepared with { Blocked = missingSemicolon };
        if (prepared is { Blocked: null } && state.ReadOnly && state.Provider.CheckReadOnly(prepared.Sql) is { } readOnlyReason)
            prepared = prepared with { Blocked = readOnlyReason };

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
                fixes = i.AllFixes,
            }),
        });
    }

    private async Task IntrospectAsync(int id, int connectionId, bool send)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var schema = await state.Connection.IntrospectAsync(CancellationToken.None);
        state.Schema = schema; // completions are answered from this cache, never by querying the database
        if (send) Event(new { type = "schema", replyTo = id, connectionId, schema });
        else Event(new { type = "ok", replyTo = id });
    }

    /// <summary>
    /// Ends a session (a closed tab). A running statement is cancelled and given time to wind down first. Closing the
    /// connection rolls back anything uncommitted: that is the point of warning the user before they close.
    /// </summary>
    private async Task DisconnectAsync(int id, int connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        CancelCurrent(state); // including a statement that has not produced a result yet: it holds the lock we are about to wait for
        if (state.Active is { } active) _queries.TryRemove(active.Id, out _);
        await state.SessionLock.WaitAsync();
        try { await state.Connection.DisposeAsync(); }
        finally { state.SessionLock.Release(); }
        Event(new { type = "ok", replyTo = id });
    }

    /// <summary>Suggestions at a cursor offset. Pure CPU over the cached schema, so it is safe to call on every keystroke.</summary>
    private void Complete(int id, int connectionId, string text, int offset)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var schema = state.Schema;
        var result = schema is null
            ? new CompletionResult(offset, offset, [])
            : CompletionEngine.Complete(schema, StatementSplitter.For(state.Provider.Parser), state.Provider.Identifiers, text, offset);
        Event(new { type = "completions", replyTo = id, start = result.Start, end = result.End, items = result.Items });
    }

    /// <summary>
    /// Runs a statement on the connection's session. The session lock is held for the statement's whole life (it is
    /// released by the query session once the reader is closed and the transaction state has been read), so nothing else
    /// can use the connection while rows are streaming from it.
    /// </summary>
    private async Task ExecuteAsync(int id, int connectionId, string sql)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        // The editor already blocks these (analyzeSheet); this catches a statement that arrives any other way.
        if (state.ReadOnly && state.Provider.CheckReadOnly(sql) is { } readOnlyReason) { Error(id, readOnlyReason); return; }
        CancelCurrent(state); // a new statement replaces the running one; this lets it wind down and free the session
        await state.SessionLock.WaitAsync();
        var handedOver = false;
        try
        {
            // The statement it replaced has wound down; if nobody drained it, free it now.
            if (state.Active is { } previous) _queries.TryRemove(previous.Id, out _);
            if (await PrepareAutoTransactionAsync(state, sql) is { } refusal) { Error(id, refusal); return; }
            // Where an error aborts the whole transaction (Postgres), the provider may guard the statement so a failure
            // undoes only it (design section 7). The guard spans the statement's whole life, up to its last row.
            var guarded = await state.Connection.Transactions.BeforeStatementAsync(sql, CancellationToken.None);
            var sw = Stopwatch.StartNew();
            var cts = new CancellationTokenSource();
            state.Starting = cts; // until the database answers, this is the only handle on the statement: cancel and close use it
            DbDataReader reader;
            try
            {
                reader = await state.Connection.ExecuteReaderAsync(sql, cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                // Cancelled before any result. Not an error, but inside a transaction it aborts it (unless the guard undoes
                // the statement): the bar must learn that.
                state.Starting = null;
                var undone = await EndGuardAsync(state, guarded, failed: true);
                await ObserveTransactionAsync(state, statement: true, rowsAffected: -1, undone);
                Event(new { type = "cancelled", replyTo = id });
                return;
            }
            catch
            {
                // An error inside a transaction aborts it (unless the guard undoes the statement): the bar has to turn red
                // even though no rows ever streamed.
                state.Starting = null;
                var undone = await EndGuardAsync(state, guarded, failed: true);
                await ObserveTransactionAsync(state, statement: true, rowsAffected: -1, undone);
                throw;
            }
            state.Starting = null;
            var writer = new RowBatchWriter(reader);
            var queryId = Interlocked.Increment(ref _nextQuery);
            var session = new QuerySession(queryId, reader, writer, cts, sw, async (rowsAffected, failed) =>
            {
                try
                {
                    var undone = await EndGuardAsync(state, guarded, failed);
                    await ObserveTransactionAsync(state, statement: true, undone ? -1 : rowsAffected, undone);
                }
                finally { state.SessionLock.Release(); }
            });
            handedOver = true;
            _queries[queryId] = session;
            state.Active = session;

            Event(new
            {
                type = "columns", replyTo = id, queryId,
                columns = writer.Columns.Select((c, i) => new { name = c.Name, kind = (int)c.Kind, type = GenericTypeOf(state.Provider, reader, i) }),
            });
            session.Start();
        }
        finally
        {
            if (!handedOver) state.SessionLock.Release();
        }
    }

    /// <summary>
    /// Ends a statement's guard, if it had one. True when the statement had failed and was undone. A guard that cannot be
    /// ended is reported, not thrown: the statement's own outcome matters more, and the state shown comes from the server.
    /// </summary>
    private async Task<bool> EndGuardAsync(ConnectionState state, bool guarded, bool failed)
    {
        if (!guarded) return false;
        try { return await state.Connection.Transactions.AfterStatementAsync(failed, CancellationToken.None); }
        catch (Exception ex)
        {
            Event(new { type = "transactionUnknown", connectionId = state.Id, message = $"The failed statement could not be undone: {ex.Message}" });
            return false;
        }
    }

    /// <summary>Begin, commit or roll back the session's transaction (the bar's buttons).</summary>
    private async Task TransactionCommandAsync(int id, int connectionId, string action)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        CancelCurrent(state);
        await state.SessionLock.WaitAsync();
        try
        {
            var monitor = state.Connection.Transactions;
            try
            {
                switch (action)
                {
                    case "begin": await monitor.BeginAsync(CancellationToken.None); break;
                    case "commit": await monitor.CommitAsync(CancellationToken.None); break;
                    case "rollback": await monitor.RollbackAsync(CancellationToken.None); break;
                    default: Error(id, $"Unknown transaction action '{action}'"); return;
                }
                Event(new { type = "ok", replyTo = id });
            }
            catch (Exception ex)
            {
                Error(id, ex.Message); // e.g. a deferred constraint refusing the commit
            }
            await ObserveTransactionAsync(state, statement: false, rowsAffected: -1);
        }
        finally
        {
            state.SessionLock.Release();
        }
    }

    /// <summary>
    /// Asks the server where the session stands and tells the UI. The caller holds the session lock, so the connection is
    /// idle. A failure here must not break the statement that triggered it, so it is reported rather than thrown.
    /// </summary>
    /// <param name="statementUndone">The statement failed and its guard undid it: the transaction carries on as before it.</param>
    private async Task ObserveTransactionAsync(ConnectionState state, bool statement, long rowsAffected, bool statementUndone = false)
    {
        try
        {
            var monitor = state.Connection.Transactions;
            var snapshot = await monitor.GetStateAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;

            // Auto-transaction: a statement failed (or was cancelled) in the automatic transaction while nothing had changed
            // in it. Nothing can be lost, so roll back rather than leave a red bar after a typo in a SELECT.
            var autoReset = false;
            if (statement && state.ResetIfItFails)
            {
                state.ResetIfItFails = false;
                if (snapshot.State == TransactionState.Failed)
                {
                    await monitor.RollbackAsync(CancellationToken.None);
                    snapshot = await monitor.GetStateAsync(CancellationToken.None);
                    autoReset = true;
                }
            }

            // An undone statement left nothing behind, so it does not count towards "3 statements · 42 rows changed".
            if (statement && !statementUndone) state.Tracker.StatementCompleted(snapshot, rowsAffected, now);
            else state.Tracker.Observed(snapshot, now);

            // Auto-transaction keeps one open: when the session is outside a transaction (it just connected, or one was
            // committed or rolled back, from the bar or typed), begin the next. The state still comes from the server.
            if (state.AutoTransaction && snapshot.State == TransactionState.None)
            {
                await monitor.BeginAsync(CancellationToken.None);
                state.Tracker.StartedAutomatically(await monitor.GetStateAsync(CancellationToken.None), DateTimeOffset.UtcNow);
            }

            var t = state.Tracker;
            Event(new
            {
                type = "transaction", connectionId = state.Id, state = t.State, startedAt = t.StartedAt,
                statements = t.Statements, rowsChanged = t.RowsChanged,
                canDetectWrites = monitor.CanDetectWrites, automatic = t.Automatic, autoReset, statementUndone,
            });
        }
        catch (Exception ex)
        {
            Event(new { type = "transactionUnknown", connectionId = state.Id, message = ex.Message });
        }
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
                type = "complete", queryId, totalRows = s.TotalRows, rowsAffected = s.RecordsAffected < 0 ? (long?)null : s.RecordsAffected,
                engineFirstBatchMs = s.FirstBatchMs, engineTotalMs = s.TotalMs,
                engineReadMs = b.ReadMs, engineAppendMs = b.AppendMs, engineWriteWaitMs = b.WriteWaitMs,
            });
            _queries.TryRemove(queryId, out _); // drained: nothing more can be fetched from it
        }
        catch (Exception ex) when (s.IsCancelled(ex))
        {
            Event(new { type = "cancelled", queryId });
            _queries.TryRemove(queryId, out _);
        }
        catch (Exception ex)
        {
            Event(new { type = "error", queryId, message = ex.Message });
            _queries.TryRemove(queryId, out _);
        }
    }

    /// <summary>
    /// Auto-transaction (design section 7), before a statement runs. The sheet is always inside a transaction, so:
    /// a statement the server refuses inside one (VACUUM, CREATE DATABASE…) ends the automatic transaction first when
    /// nothing has changed in it, and is refused while there are changes, so nothing is committed behind the user's back.
    /// Returns why the statement must not run, or null. The caller holds the session lock.
    /// </summary>
    private async Task<string?> PrepareAutoTransactionAsync(ConnectionState state, string sql)
    {
        state.ResetIfItFails = false;
        if (!state.AutoTransaction) return null;
        var monitor = state.Connection.Transactions;
        var current = (await monitor.GetStateAsync(CancellationToken.None)).State;
        var unchangedAutomatic = current == TransactionState.Open && state.Tracker.Automatic;

        if (state.Provider.CannotRunInTransaction(sql))
        {
            if (current == TransactionState.Failed) return "This statement cannot run inside a transaction. Roll back first.";
            if (current == TransactionState.Changed)
                return "This statement cannot run inside a transaction, and this one has changes. Commit or roll back first.";
            if (unchangedAutomatic) await monitor.CommitAsync(CancellationToken.None); // nothing in it: ending it loses nothing
            return null; // a new automatic transaction begins once the statement has run
        }

        state.ResetIfItFails = unchangedAutomatic;
        return null;
    }

    /// <summary>Cancels whatever the session is doing: a statement still waiting for its first result, or one streaming rows.</summary>
    private static void CancelCurrent(ConnectionState state)
    {
        state.Starting?.Cancel();
        state.Active?.Cancel();
    }

    /// <summary>A result column's generic type, for result styling. A reader that cannot name the type gives Text.</summary>
    private static GenericType GenericTypeOf(IDatabaseProvider provider, DbDataReader reader, int ordinal)
    {
        try { return provider.GenericTypeOf(reader.GetDataTypeName(ordinal)); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IndexOutOfRangeException) { return GenericType.Text; }
    }

    private void Event(object payload) => _out.SendEvent(JsonSerializer.Serialize(payload, Json));
    private void Error(int replyTo, string message) => Event(new { type = "error", replyTo, message });

    public async ValueTask DisposeAsync()
    {
        foreach (var q in _queries.Values) q.Cancel();
        AbandonExports();
        foreach (var c in _connections.Values) await c.Connection.DisposeAsync();
    }

    private sealed class ConnectionState(int id, IDatabaseProvider provider, IProviderConnection connection)
    {
        public int Id { get; } = id;

        /// <summary>One thing at a time on the session connection: a statement (for its whole streaming life), a state
        /// probe, or a begin/commit/rollback.</summary>
        public SemaphoreSlim SessionLock { get; } = new(1, 1);

        public TransactionTracker Tracker { get; } = new();
        public IDatabaseProvider Provider { get; } = provider;
        public IProviderConnection Connection { get; } = connection;
        public QuerySession? Active { get; set; }

        /// <summary>Cancels a statement that has been sent but has not produced a result yet (so has no query id).</summary>
        public CancellationTokenSource? Starting { get; set; }
        public DatabaseSchema? Schema { get; set; }

        /// <summary>The saved connection this session was opened from; null for a pasted connection string.</summary>
        public string? SavedId { get; init; }

        /// <summary>Opened read-only: the server refuses writes, and statements that would switch that off are not sent.</summary>
        public bool ReadOnly { get; init; }

        /// <summary>Keep a transaction open on this session, so every change waits for Commit (never on a read-only session).</summary>
        public bool AutoTransaction { get; init; }

        /// <summary>The running statement started in an automatic transaction with no changes: if it fails, roll back.</summary>
        public bool ResetIfItFails { get; set; }
    }
}
