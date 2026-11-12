using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// The Postgres category of the settings editor (design section 10). One instance per provider, shared by its sessions:
/// a saved change applies from each session's next statement.
/// </summary>
internal sealed class PostgresSettings
{
    public const string OnErrorKey = "onError";
    public const string UndoStatement = "undoStatement";
    public const string AbortTransaction = "abortTransaction";

    public static readonly IReadOnlyList<ProviderSetting> All =
    [
        new(OnErrorKey, "When a statement fails inside a transaction", UndoStatement,
        [
            new(UndoStatement, "Undo just that statement (recommended)",
                "The transaction stays open and keeps the changes made before it, so a typo does not cost you your work. " +
                "SiftDB sets a savepoint before each statement and rolls back to it if the statement fails."),
            new(AbortTransaction, "Abort the transaction",
                "PostgreSQL's own behaviour: any error fails the whole transaction, and only a rollback gets out of it."),
        ],
        "Applies to statements run inside a transaction. Scripts stop at the first error either way."),
    ];

    private volatile bool _undoFailedStatement = true;

    public bool UndoFailedStatement => _undoFailedStatement;

    public void Apply(IReadOnlyDictionary<string, string> values) =>
        _undoFailedStatement = values.GetValueOrDefault(OnErrorKey, UndoStatement) != AbortTransaction;
}
