using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>
/// The provider names its own secret and says when it is needed (contract v14, design section 14, F19), shaped like
/// DynamoDB's: a secret access key, only with Auth set to Access keys.
/// </summary>
public sealed class ProviderSecretTests : IAsyncLifetime
{
    private sealed class KeysProvider(SecretSpec? secret) : IDatabaseProvider
    {
        public readonly ConcurrentQueue<string?> Secrets = new();
        public string Id => "keys";
        public string DisplayName => "Keys DB";
        public int ContractVersion => ProviderContract.CurrentVersion;
        public SecretSpec? Secret => secret;
        public ISqlParser Parser => throw new NotSupportedException();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();
        public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
        [
            new("auth", "Auth", ConnectionFieldKind.Choice, "server", Default: "profile", Choices: [new("profile", "AWS profile"), new("keys", "Access keys")]),
            new("region", "Region", ConnectionFieldKind.Text, "server"),
        ];
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;
        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password)
        {
            Secrets.Enqueue(password);
            var auth = fields.GetValueOrDefault("auth") is { Length: > 0 } a ? a : "profile";
            return $"auth={auth};region={fields.GetValueOrDefault("region")};secret={password}";
        }
        public ParsedConnection ParseConnectionString(string cs) => new(new Dictionary<string, string>(), null);
        public string? CheckReadOnly(string sql) => null;
        public bool CannotRunInTransaction(string sql) => false;
        public GenericType GenericTypeOf(string dataTypeName) => GenericType.Text;

        public Task<IProviderConnection> OpenAsync(string cs, ConnectionOptions options, CancellationToken ct)
        {
            if (cs.StartsWith("auth=keys", StringComparison.Ordinal) && !cs.EndsWith("secret=right", StringComparison.Ordinal))
                throw new ConnectionAuthenticationException("The security token included in the request is invalid.", new Exception());
            if (cs.StartsWith("auth=profile", StringComparison.Ordinal) && cs.Contains("expired"))
                throw new ConnectionAuthenticationException("The SSO session has expired. Run aws sso login.", new Exception());
            return Task.FromResult<IProviderConnection>(new Conn());
        }

        private sealed class Conn : IProviderConnection
        {
            public Task<DbDataReader> ExecuteReaderAsync(string sql, CancellationToken ct) => Task.FromResult<DbDataReader>(new DataTable().CreateDataReader());
            public ITransactionMonitor Transactions { get; } = new Monitor();
            public Task<DatabaseSchema> IntrospectAsync(CancellationToken ct) => Task.FromResult(new DatabaseSchema([]));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Monitor : ITransactionMonitor
        {
            public bool CanDetectWrites => false;
            public Task BeginAsync(CancellationToken ct) => Task.CompletedTask;
            public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<TransactionSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(new TransactionSnapshot(TransactionState.None, null));
        }
    }

    private sealed class Sink : IEngineOutput
    {
        public readonly ConcurrentQueue<string> Raw = new();
        public void SendEvent(string json) => Raw.Enqueue(json);
        public void SendBatch(int queryId, EncodedBatch batch) { }
    }

    private static readonly SecretSpec AccessKey = new("Secret access key", new FieldCondition("auth", ["keys"]), "From the IAM user's access key.");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-engine-secret-");
    private readonly MemorySecrets _secrets = new();
    private readonly Sink _sink = new();
    private KeysProvider _provider = new(AccessKey);
    private EngineHost _engine = null!;
    private int _nextId;

    public Task InitializeAsync() => Start(AccessKey);

    private async Task Start(SecretSpec? secret)
    {
        if (_engine is not null) await _engine.DisposeAsync();
        _provider = new KeysProvider(secret);
        _engine = new EngineHost([_provider], _sink, new ConnectionStore(Path.Combine(_dir.FullName, "connections.json"), _secrets));
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    private async Task<JsonElement> Send(object command)
    {
        var id = ++_nextId;
        var json = JsonSerializer.SerializeToNode(command)!.AsObject();
        json["id"] = id;
        await _engine.HandleCommandAsync(json.ToJsonString());
        return _sink.Raw.Select(e => JsonDocument.Parse(e).RootElement.Clone()).Last(e => e.TryGetProperty("replyTo", out var r) && r.GetInt32() == id);
    }

    private async Task<string> Save(string auth, bool remember, string? secret = null, string? id = null, string region = "eu-west-2")
    {
        object connection = new { id, name = "orders", provider = "keys", fields = new { auth, region }, rememberPassword = remember, readOnly = false };
        var reply = secret is null
            ? await Send(new { type = "saveConnection", connection })
            : await Send(new { type = "saveConnection", connection, password = secret });
        Assert.Equal("connectionSaved", reply.GetProperty("type").GetString());
        return reply.GetProperty("savedId").GetString()!;
    }

    [Fact]
    public async Task The_provider_describes_its_secret_to_the_form()
    {
        var p = (await Send(new { type = "listConnections" })).GetProperty("providers").EnumerateArray().Single();
        var secret = p.GetProperty("secret");
        Assert.Equal("Secret access key", secret.GetProperty("label").GetString());
        Assert.Equal("auth", secret.GetProperty("neededWhen").GetProperty("key").GetString());
        Assert.Equal(["keys"], secret.GetProperty("neededWhen").GetProperty("values").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task With_a_profile_it_connects_without_asking_even_when_nothing_is_remembered()
    {
        var id = await Save("profile", remember: false);
        var reply = await Send(new { type = "connect", savedId = id });
        Assert.Equal("connected", reply.GetProperty("type").GetString());
        Assert.Null(_provider.Secrets.Last()); // the provider got no secret
    }

    [Fact]
    public async Task With_access_keys_it_asks_and_the_prompt_uses_the_secrets_name()
    {
        var id = await Save("keys", remember: false);
        var reply = await Send(new { type = "connect", savedId = id });
        Assert.Equal("passwordNeeded", reply.GetProperty("type").GetString());
        Assert.Equal("Secret access key", reply.GetProperty("label").GetString());

        var wrong = await Send(new { type = "connect", savedId = id, password = "wrong" });
        Assert.Equal("passwordNeeded", wrong.GetProperty("type").GetString());
        Assert.True(wrong.GetProperty("retry").GetBoolean());
        Assert.Equal("Secret access key", wrong.GetProperty("label").GetString());

        Assert.Equal("connected", (await Send(new { type = "connect", savedId = id, password = "right" })).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Switching_to_a_profile_deletes_the_saved_secret()
    {
        var id = await Save("keys", remember: true, secret: "right");
        Assert.Equal("right", _secrets.Get(id));
        await Save("profile", remember: true, id: id);
        Assert.Null(_secrets.Get(id)); // an unused credential is not left on disk
        // Back to access keys: the key has to be typed again.
        await Save("keys", remember: true, id: id);
        Assert.Equal("connected", (await Send(new { type = "connect", savedId = id, password = "right" })).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_secret_sent_for_a_profile_connection_is_not_kept()
    {
        var id = await Save("profile", remember: true, secret: "stray");
        Assert.Null(_secrets.Get(id));
    }

    [Fact]
    public async Task An_empty_auth_field_counts_as_its_default()
    {
        var id = await Save("", remember: false);
        Assert.Equal("connected", (await Send(new { type = "connect", savedId = id })).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_refused_login_without_a_secret_says_the_servers_reason_and_never_asks()
    {
        var id = await Save("profile", remember: false, region: "expired");
        // The fake refuses this profile: there is no secret to ask for, so the reason is the answer.
        var reply = await Send(new { type = "connect", savedId = id });
        Assert.Equal("error", reply.GetProperty("type").GetString());
        Assert.Contains("SSO session has expired", reply.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Test_says_what_was_refused_in_the_secrets_words()
    {
        var connection = new { name = "t", provider = "keys", fields = new { auth = "keys" }, rememberPassword = false, readOnly = false };
        var none = await Send(new { type = "testConnection", connection });
        Assert.StartsWith("The server wants a secret access key.", none.GetProperty("message").GetString());
        var wrong = await Send(new { type = "testConnection", connection, password = "wrong" });
        Assert.StartsWith("The server turned the secret access key down.", wrong.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_provider_without_a_secret_never_asks()
    {
        await Start(null);
        var id = await Save("keys", remember: false);
        Assert.Equal(JsonValueKind.Null, (await Send(new { type = "listConnections" })).GetProperty("providers")[0].GetProperty("secret").ValueKind);
        var reply = await Send(new { type = "connect", savedId = id });
        Assert.NotEqual("passwordNeeded", reply.GetProperty("type").GetString());
        Assert.Null(_provider.Secrets.Last());
    }

    [Fact]
    public void An_always_needed_secret_is_the_default()
    {
        IDatabaseProvider plain = new KeysProviderDefault();
        Assert.Equal(new SecretSpec("Password"), plain.Secret);
    }

    /// <summary>A provider that says nothing about its secret: it gets the default.</summary>
    private sealed class KeysProviderDefault : IDatabaseProvider
    {
        public string Id => "plain";
        public string DisplayName => "Plain";
        public int ContractVersion => ProviderContract.CurrentVersion;
        public ISqlParser Parser => throw new NotSupportedException();
        public IIdentifierPolicy Identifiers => throw new NotSupportedException();
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
        public ReadOnlyEnforcement ReadOnlyEnforcement => ReadOnlyEnforcement.Client;
        public string BuildConnectionString(IReadOnlyDictionary<string, string> fields, string? password) => "";
        public ParsedConnection ParseConnectionString(string cs) => throw new NotSupportedException();
        public string? CheckReadOnly(string sql) => null;
        public bool CannotRunInTransaction(string sql) => false;
        public GenericType GenericTypeOf(string dataTypeName) => GenericType.Text;
        public Task<IProviderConnection> OpenAsync(string cs, ConnectionOptions options, CancellationToken ct) => throw new NotSupportedException();
    }
}
