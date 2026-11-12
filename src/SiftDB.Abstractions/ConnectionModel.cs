namespace SiftDB.Abstractions;

/// <summary>How a connection form shows a field. The UI draws the form from these, so it needs no provider knowledge.</summary>
public enum ConnectionFieldKind
{
    Text,
    Number,
    /// <summary>One of <see cref="ConnectionField.Choices"/>.</summary>
    Choice,
    /// <summary>A file on this machine, such as a CA certificate.</summary>
    FilePath,
    /// <summary>Extra driver keywords in the driver's own syntax (<c>Timeout=30;Application Name=x</c>).</summary>
    Keywords,
}

public sealed record ConnectionChoice(string Value, string Label);

/// <summary>True when the field <paramref name="Key"/> holds one of <paramref name="Values"/>.</summary>
public sealed record FieldCondition(string Key, IReadOnlyList<string> Values);

/// <summary>
/// One field of a saved connection (design section 8). The password is not a field: it is a secret, kept apart from the
/// connection and handled by the engine's store.
/// </summary>
/// <param name="Key">Stable key the value is stored under. Never rename one: saved connections use it.</param>
/// <param name="Group">Where the form puts it: "server", "security" or "advanced".</param>
/// <param name="EnabledWhen">Shown enabled only when another field has one of these values (key, values), e.g. a CA
/// file only matters when the certificate is verified.</param>
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldKind Kind,
    string Group,
    string? Default = null,
    bool Required = false,
    string? Placeholder = null,
    string? Help = null,
    IReadOnlyList<ConnectionChoice>? Choices = null,
    FieldCondition? EnabledWhen = null);

/// <summary>How a session is opened, beyond where to.</summary>
/// <param name="ReadOnly">The session refuses writes (design section 8).</param>
public sealed record ConnectionOptions(bool ReadOnly = false)
{
    public static readonly ConnectionOptions Default = new();
}

/// <summary>A connection string taken apart into a provider's fields, for saving a pasted string.</summary>
/// <param name="Fields">Values by <see cref="ConnectionField.Key"/>. Keywords with no field of their own are left in the
/// provider's <see cref="ConnectionFieldKind.Keywords"/> field.</param>
public sealed record ParsedConnection(IReadOnlyDictionary<string, string> Fields, string? Password);

/// <summary>Who stops writes on a read-only session.</summary>
public enum ReadOnlyEnforcement
{
    /// <summary>The server refuses them (Postgres: <c>default_transaction_read_only</c>); the app only stops statements that
    /// would switch that off.</summary>
    Server,
    /// <summary>Only the app's own check stands between a statement and the database.</summary>
    Client,
}

/// <summary>
/// Thrown by <see cref="IDatabaseProvider.OpenAsync"/> when the server turned the login down for its password (wrong, or
/// needed and not given), so the app can ask for it again instead of just showing an error.
/// </summary>
public sealed class ConnectionAuthenticationException(string message, Exception inner) : Exception(message, inner);
