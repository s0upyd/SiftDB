using Npgsql;
using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// A saved Postgres connection's fields (design section 8), and the round trip to and from an Npgsql connection string.
/// Keywords with no field of their own (Timeout, Application Name, Options…) live in the "extra" field, in Npgsql's syntax.
/// </summary>
internal static class PostgresConnectionFields
{
    public const string Host = "host", Port = "port", Database = "database", Username = "username",
        SslMode = "sslMode", RootCertificate = "rootCertificate", Extra = "extra";

    private static readonly string[] VerifyModes = ["VerifyCA", "VerifyFull"];

    public static readonly IReadOnlyList<ConnectionField> All =
    [
        new(Host, "Host", ConnectionFieldKind.Text, "server", Default: "localhost", Required: true),
        new(Port, "Port", ConnectionFieldKind.Number, "server", Default: "5432"),
        new(Database, "Database", ConnectionFieldKind.Text, "server", Required: true, Placeholder: "postgres"),
        new(Username, "User", ConnectionFieldKind.Text, "server", Placeholder: "postgres"),
        new(SslMode, "SSL", ConnectionFieldKind.Choice, "security", Default: "Prefer",
            Help: "Verify CA checks the server's certificate against a CA you trust; Verify full also checks the host name.",
            Choices:
            [
                new("Disable", "Disable: never use SSL"),
                new("Allow", "Allow: SSL only if the server insists"),
                new("Prefer", "Prefer: SSL if the server offers it"),
                new("Require", "Require: SSL, but do not check the certificate"),
                new("VerifyCA", "Verify CA: SSL, certificate from a trusted CA"),
                new("VerifyFull", "Verify full: SSL, trusted certificate for this host"),
            ]),
        new(RootCertificate, "CA certificate", ConnectionFieldKind.FilePath, "security",
            Placeholder: @"C:\certs\root.crt", Help: "The CA that signed the server's certificate. Empty uses the Windows certificate store.",
            EnabledWhen: new FieldCondition(SslMode, VerifyModes)),
        new(Extra, "More keywords", ConnectionFieldKind.Keywords, "advanced", Placeholder: "Timeout=30;Application Name=SiftDB",
            Help: "Any other Npgsql connection string keywords."),
    ];

    /// <summary>The fields Npgsql knows by these names; removed from a parsed string so what is left is "extra".</summary>
    private static readonly string[] Named = ["Host", "Port", "Database", "Username", "Password", "SSL Mode", "Root Certificate"];

    public static string Build(IReadOnlyDictionary<string, string> fields, string? password)
    {
        string? Get(string key) => fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        // Extra keywords first, so a named field always wins over the same keyword typed into "More keywords".
        var b = new NpgsqlConnectionStringBuilder(Get(Extra) ?? "");
        b.Host = Get(Host) ?? "localhost";
        if (Get(Port) is { } port)
            b.Port = int.TryParse(port, out var p) && p is > 0 and < 65536 ? p : throw new ArgumentException($"Port '{port}' is not a port number.");
        if (Get(Database) is { } db) b.Database = db;
        if (Get(Username) is { } user) b.Username = user;
        if (!string.IsNullOrEmpty(password)) b.Password = password;
        if (Get(SslMode) is { } ssl)
            b.SslMode = Enum.TryParse<SslMode>(ssl, ignoreCase: true, out var mode) ? mode : throw new ArgumentException($"Unknown SSL mode '{ssl}'.");
        if (Get(RootCertificate) is { } ca) b.RootCertificate = ca;
        return b.ConnectionString;
    }

    public static ParsedConnection Parse(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString); // throws on keywords Npgsql does not know
        var fields = new Dictionary<string, string>();
        void Put(string key, string? value) { if (!string.IsNullOrEmpty(value)) fields[key] = value; }
        Put(Host, b.Host);
        // ContainsKey is true for every keyword Npgsql knows, given or not; a value equal to the default says the same thing anyway.
        var defaults = new NpgsqlConnectionStringBuilder();
        if (b.Port != defaults.Port) Put(Port, b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Put(Database, b.Database);
        Put(Username, b.Username);
        if (b.SslMode != defaults.SslMode) Put(SslMode, b.SslMode.ToString());
        Put(RootCertificate, b.RootCertificate);
        var password = b.Password;

        // Npgsql maps synonyms (Server, UID, PWD…) to its own names, so removing those leaves exactly the other keywords.
        foreach (var k in Named) b.Remove(k);
        Put(Extra, b.ConnectionString);
        return new ParsedConnection(fields, string.IsNullOrEmpty(password) ? null : password);
    }

    /// <summary>
    /// Read-only goes into the startup options rather than a SET after connecting: then it is the session's default, and
    /// RESET, RESET ALL and DISCARD ALL fall back to it instead of switching it off.
    /// </summary>
    public static string WithOptions(string connectionString, ConnectionOptions options)
    {
        if (!options.ReadOnly) return connectionString;
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        b.Options = string.IsNullOrWhiteSpace(b.Options)
            ? "-c default_transaction_read_only=on"
            : $"{b.Options} -c default_transaction_read_only=on"; // last one wins, so typed options cannot undo it
        return b.ConnectionString;
    }
}
