namespace SiftDB.Abstractions;

/// <summary>What the rows of a dry run hold (design section 14, F2).</summary>
public enum DryRunShape
{
    /// <summary>An UPDATE: each row is the table's columns before, then the same columns after.</summary>
    BeforeAndAfter,
    /// <summary>An UPDATE on a database that cannot return the old values: the columns after only.</summary>
    AfterOnly,
    /// <summary>A DELETE: the rows that would go.</summary>
    Deleted,
}

/// <summary>How to dry-run a statement, or why it cannot be. <paramref name="Sql"/> is what to run instead of the
/// statement: the same change, made to return the affected rows.</summary>
public sealed record DryRunPlan(string Sql, DryRunShape Shape, string? Refused)
{
    public static DryRunPlan No(string why) => new("", DryRunShape.Deleted, why);
}

/// <summary>
/// Runs a data-changing statement for real, inside something that is then rolled back, so its affected rows can be
/// shown without anything changing (design section 14, F2). A connection that offers it returns it from
/// <see cref="IProviderConnection.DryRun"/>.
/// </summary>
public interface IDryRunSupport
{
    /// <summary>
    /// The rewritten statement, or why it cannot be dry-run (more than one statement, not an UPDATE or DELETE, a
    /// data-changing WITH, SQL that does not parse). Pure: nothing is sent.
    /// </summary>
    DryRunPlan Plan(string sql);

    /// <summary>Starts what <see cref="EndAsync"/> undoes: a savepoint inside an open transaction, a transaction outside one.
    /// Called with the session idle, and never in a failed transaction.</summary>
    Task BeginAsync(CancellationToken ct);

    /// <summary>Undoes everything since <see cref="BeginAsync"/>, whether the statement finished, failed or was cancelled,
    /// leaving the session as it was before. Throws if that could not be done: the caller must say so loudly.</summary>
    Task EndAsync(CancellationToken ct);
}
