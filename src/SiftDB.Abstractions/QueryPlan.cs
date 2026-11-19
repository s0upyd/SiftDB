namespace SiftDB.Abstractions;

/// <summary>
/// One step of a query plan (design section 14, F6), provider-neutral. Row counts and times are per loop, as databases
/// report them; <see cref="Loops"/> says how many times the step ran. Actual values are null when the plan was not run
/// (estimates only).
/// </summary>
/// <param name="Operation">What the step does, as the database names it: "Seq Scan", "Hash Left Join".</param>
/// <param name="Label">How the step reads in a plan: "Seq Scan on orders o", "Index Scan using orders_pkey on orders o".</param>
/// <param name="Alias">The name the statement uses for <see cref="Relation"/> (its alias, else its name), or the name of a
/// CTE, function or subquery the step reads. Matched to the statement's table references to link the plan to the SQL.</param>
/// <param name="TableRows">The database's estimate of the rows in <see cref="Relation"/>, when known: how big the table is,
/// not how many rows the step returns.</param>
/// <param name="ScansWholeTable">The step reads every row of its table (a sequential scan).</param>
/// <param name="IsNestedLoop">The step is a nested-loop join: its second child runs once per row of its first.</param>
/// <param name="Spill">Why the step had to use disk (a sort or hash that did not fit in memory), said for a person, or null.</param>
/// <param name="ChangesData">The step inserts, updates or deletes rows.</param>
/// <param name="Properties">Everything the database said about the step, in its own words, for the details panel.</param>
public sealed record PlanNode(
    string Operation,
    string Label,
    string? Schema,
    string? Relation,
    string? Alias,
    string? Index,
    double EstimatedRows,
    double? ActualRows,
    double? Loops,
    double StartupCost,
    double TotalCost,
    double? ActualStartupMs,
    double? ActualTotalMs,
    string? Filter,
    string? Condition,
    double? RowsRemovedByFilter,
    double? TableRows,
    bool ScansWholeTable,
    bool IsNestedLoop,
    string? Spill,
    bool ChangesData,
    IReadOnlyList<PlanProperty> Properties,
    IReadOnlyList<PlanNode> Children);

/// <summary>One fact about a plan step, as the database names it ("Sort Key", "Buffers").</summary>
public sealed record PlanProperty(string Name, string Value);

/// <summary>A statement's plan.</summary>
/// <param name="Analyzed">The statement ran (EXPLAIN ANALYZE), so actual rows and times are there.</param>
/// <param name="Text">The plan in the database's usual text form, for pasting elsewhere.</param>
public sealed record QueryPlan(PlanNode Root, bool Analyzed, double? PlanningMs, double? ExecutionMs, string Text);

/// <summary>
/// Explains statements on a session (design section 14, F6). A connection that offers it returns it from
/// <see cref="IProviderConnection.Plans"/>.
/// </summary>
public interface IPlanSupport
{
    /// <summary>
    /// The plan of one statement. With <paramref name="analyze"/> the statement really runs (and its timings are
    /// measured): the caller wraps it in a dry run's savepoint so nothing it does is kept. Runs on the session, so it
    /// sees the session's temp tables and settings. Called with the session idle.
    /// </summary>
    Task<QueryPlan> ExplainAsync(string sql, bool analyze, CancellationToken ct);
}
