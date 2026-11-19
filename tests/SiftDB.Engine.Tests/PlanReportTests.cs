using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>How a plan is sized and what is said about it (design section 14, F6), from hand-made plans.</summary>
public class PlanReportTests
{
    private sealed class NoParser : ISqlParser
    {
        public IReadOnlyList<SqlToken> Tokenize(string sql) => throw new NotSupportedException();
        public IReadOnlyList<SqlRange> Split(string sql) => throw new NotSupportedException();
        public SqlSyntaxError? Validate(string statement) => null;
    }

    private sealed class Lower : IIdentifierPolicy
    {
        public string Quote(string identifier) => identifier;
        public string Fold(string unquotedIdentifier) => unquotedIdentifier.ToLowerInvariant();
    }

    private static PlanNode Step(string op, double est, double? actual = null, double? loops = null, double? ms = null, double cost = 10,
        string? relation = null, string? filter = null, double? removed = null, double? tableRows = null, params PlanNode[] children) =>
        new(op, relation is null ? op : $"{op} on {relation}", relation is null ? null : "public", relation, relation, null,
            est, actual, loops, 0, cost, ms is null ? null : 0, ms, filter, null, removed, tableRows,
            ScansWholeTable: op == "Seq Scan", IsNestedLoop: op == "Nested Loop", Spill: null, ChangesData: false, [], children);

    private static PlanReport Report(PlanNode root, bool analyzed = true) =>
        PlanReporter.Build(new QueryPlan(root, analyzed, 0.1, 5, ""), new NoParser(), new Lower(), null, null, 0);

    [Fact]
    public void Widths_count_every_loop_and_self_is_what_children_do_not_account_for()
    {
        var inner = Step("Index Scan", 1, actual: 1, loops: 50, ms: 0.02, relation: "items");
        var outer = Step("Seq Scan", 50, actual: 50, loops: 1, ms: 2, relation: "orders");
        var root = Report(Step("Nested Loop", 50, actual: 50, loops: 1, ms: 5, children: [outer, inner])).Root;
        Assert.Equal(5, root.Value);
        Assert.Equal(1, root.Children[1].Value, 6);   // 0.02 ms × 50 loops
        Assert.Equal(2, root.Self, 6);                 // 5 − 2 − 1
        Assert.Equal([0, 1, 2], new[] { root.Id, root.Children[0].Id, root.Children[1].Id });
    }

    [Fact]
    public void Estimates_use_total_cost()
    {
        var r = Report(Step("Sort", 100, cost: 80, children: [Step("Seq Scan", 100, cost: 30, relation: "t")]), analyzed: false);
        Assert.Equal("cost", r.Measure);
        Assert.Equal(80, r.Root.Value);
        Assert.Equal(50, r.Root.Self);
    }

    [Fact]
    public void A_bad_estimate_is_flagged_where_it_starts_not_in_every_step_above()
    {
        var scan = Step("Seq Scan", 12, actual: 48_000, loops: 1, ms: 30, relation: "orders");
        var root = Report(Step("Hash Join", 12, actual: 48_000, loops: 1, ms: 50, children: [scan])).Root;
        var advice = Assert.Single(root.Children[0].Advice);
        Assert.Equal("estimate", advice.Kind);
        Assert.Equal("The planner expected 12 rows, got 48,000. Stale statistics? Try ANALYZE orders.", advice.Message);
        Assert.Empty(root.Advice);
    }

    [Fact]
    public void Small_or_close_estimates_are_not_flagged()
    {
        Assert.Empty(Report(Step("Seq Scan", 1, actual: 9, loops: 1, ms: 1, relation: "t")).Root.Advice);   // tiny
        Assert.Empty(Report(Step("Seq Scan", 1000, actual: 3000, loops: 1, ms: 1, relation: "t")).Root.Advice); // 3×
    }

    [Fact]
    public void A_big_scan_that_keeps_few_rows_is_flagged_and_one_that_keeps_most_is_not()
    {
        var picky = Report(Step("Seq Scan", 200, actual: 200, loops: 1, ms: 3, relation: "t", filter: "(x = 5)", removed: 19_800)).Root;
        Assert.Equal("Reads 20,000 rows of t to keep 200. An index on the filtered columns may help.", Assert.Single(picky.Advice).Message);
        var most = Report(Step("Seq Scan", 19_000, actual: 19_000, loops: 1, ms: 3, relation: "t", filter: "(x > 5)", removed: 1_000)).Root;
        Assert.Empty(most.Advice);
        var estimated = Report(Step("Seq Scan", 50, relation: "t", filter: "(x = 5)", tableRows: 1_000_000), analyzed: false).Root;
        Assert.Contains("about 1,000,000 rows", Assert.Single(estimated.Advice).Message);
    }

    [Fact]
    public void A_nested_loop_with_many_inner_runs_is_flagged()
    {
        var inner = Step("Index Scan", 1, actual: 1, loops: 5_000, ms: 0.01, relation: "items");
        var outer = Step("Seq Scan", 5_000, actual: 5_000, loops: 1, ms: 2, relation: "orders");
        var root = Report(Step("Nested Loop", 5_000, actual: 5_000, loops: 1, ms: 60, children: [outer, inner])).Root;
        Assert.StartsWith("The inner side ran 5,000 times.", Assert.Single(root.Advice).Message);
    }
}
