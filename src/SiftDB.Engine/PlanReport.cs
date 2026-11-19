using System.Globalization;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>Something worth knowing about a plan step (design section 14, F6), said for a person.</summary>
/// <param name="Kind">estimate (rows far from the planner's guess), wholeTable (a big table read to keep a few rows),
/// spill (memory ran out, disk was used) or loops (a nested loop running its inner side many times).</param>
public sealed record PlanAdvice(string Kind, string Message);

/// <summary>Where a plan step's table is written in the sheet: offsets in the editor's text.</summary>
public sealed record PlanSqlRange(int Start, int Length);

/// <summary>
/// A plan step as the UI draws it. <see cref="Value"/> is what its width shows: total time in ms (an analysed plan,
/// counting every loop) or total cost (estimates), its children included. <see cref="Self"/> is the part spent in the step
/// itself, which its colour shows, so the step that costs the most stands out.
/// </summary>
public sealed record PlanReportNode(
    int Id, string Operation, string Label, double Value, double Self, double? Ms, double Cost,
    double EstimatedRows, double? ActualRows, double? Loops,
    IReadOnlyList<PlanAdvice> Advice, IReadOnlyList<PlanSqlRange> Sql, IReadOnlyList<PlanProperty> Properties,
    IReadOnlyList<PlanReportNode> Children);

/// <param name="Measure">"time" (analysed: widths are milliseconds) or "cost" (estimates: the planner's cost units).</param>
/// <param name="ChangedData">The statement inserts, updates or deletes: analysed, it ran and was rolled back.</param>
public sealed record PlanReport(
    bool Analyzed, string Measure, double? PlanningMs, double? ExecutionMs, bool ChangedData, string Text, PlanReportNode Root);

/// <summary>
/// Turns a provider's plan into what the plan tab shows (design section 14, F6): sizes for the icicle graph, the advice
/// that teaches (bad estimates, big scans, spills, long nested loops), and the table references in the SQL each step
/// reads, so clicking a step can select its table in the editor. Provider-neutral: it reads only <see cref="PlanNode"/>.
/// </summary>
public static class PlanReporter
{
    /// <summary>A sequential scan reading at least this many rows is worth a remark.</summary>
    public const double BigTableRows = 10_000;

    /// <summary>Estimated and actual rows this many times apart are a bad estimate.</summary>
    public const double EstimateFactor = 10;

    /// <summary>A nested loop running its inner side at least this many times is worth a remark.</summary>
    public const double ManyLoops = 1_000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="source">The statement as written in the editor (before any Assisted corrections), or null to skip
    /// linking; <paramref name="sourceStart"/> is where it starts in the editor's text.</param>
    public static PlanReport Build(QueryPlan plan, ISqlParser parser, IIdentifierPolicy policy, DatabaseSchema? schema,
        string? source, int sourceStart)
    {
        var refs = source is null ? [] : TableRefs(parser, policy, schema, source, sourceStart);
        var next = 0;
        var root = Node(plan.Root, plan.Analyzed, refs, ref next);
        return new PlanReport(plan.Analyzed, plan.Analyzed ? "time" : "cost", plan.PlanningMs, plan.ExecutionMs,
            AnyChanges(plan.Root), plan.Text, root);
    }

    private static bool AnyChanges(PlanNode n) => n.ChangesData || n.Children.Any(AnyChanges);

    private static PlanReportNode Node(PlanNode n, bool analyzed, List<TableRef> refs, ref int next)
    {
        var id = next++;
        var children = new List<PlanReportNode>();
        foreach (var c in n.Children) children.Add(Node(c, analyzed, refs, ref next));

        double? ms = analyzed ? (n.ActualTotalMs ?? 0) * (n.Loops ?? 0) : null;
        var value = ms ?? n.TotalCost;
        var self = Math.Max(0, value - children.Sum(c => c.Value));
        var advice = Advise(n, analyzed, children);
        var sql = n.Alias is null ? [] : refs.Where(r => r.Matches(n)).Select(r => r.Range).ToList();
        return new PlanReportNode(id, n.Operation, n.Label, value, self, ms, n.TotalCost, n.EstimatedRows, n.ActualRows, n.Loops,
            advice, sql, n.Properties, children);
    }

    private static List<PlanAdvice> Advise(PlanNode n, bool analyzed, List<PlanReportNode> children)
    {
        var advice = new List<PlanAdvice>();
        var ran = analyzed && n.Loops is > 0;

        // A bad estimate shows up in every step above the one that made it: say it where it starts.
        if (ran && n.ActualRows is { } actual && !children.Any(c => c.Advice.Any(a => a.Kind == "estimate")))
        {
            var est = n.EstimatedRows;
            var ratio = Math.Max(Math.Max(est, 1) / Math.Max(actual, 1), Math.Max(actual, 1) / Math.Max(est, 1));
            if (ratio >= EstimateFactor && Math.Max(est, actual) * n.Loops!.Value >= 100)
            {
                var perLoop = n.Loops > 1 ? " per loop" : "";
                var why = n.Relation is { } rel
                    ? $" Stale statistics? Try ANALYZE {rel}."
                    : " Estimates this far off can lead the planner to a slow plan.";
                advice.Add(new PlanAdvice("estimate", $"The planner expected {Count(est)} rows{perLoop}, got {Count(actual)}.{why}"));
            }
        }

        if (n.ScansWholeTable && n.Filter is not null)
        {
            var name = n.Relation ?? n.Alias ?? "the table";
            if (ran && n.ActualRows is { } kept)
            {
                var read = (kept + (n.RowsRemovedByFilter ?? 0)) * n.Loops!.Value;
                var keptAll = kept * n.Loops!.Value;
                if (read >= BigTableRows && keptAll < read / 10)
                    advice.Add(new PlanAdvice("wholeTable",
                        $"Reads {Count(read)} rows of {name} to keep {Count(keptAll)}. An index on the filtered columns may help."));
            }
            else if (!analyzed && n.TableRows is { } rows && rows >= BigTableRows && n.EstimatedRows < rows / 10)
            {
                advice.Add(new PlanAdvice("wholeTable",
                    $"Reads all of {name} (about {Count(rows)} rows) to keep about {Count(n.EstimatedRows)}. An index on the filtered columns may help."));
            }
        }

        if (n.Spill is { } spill) advice.Add(new PlanAdvice("spill", spill));

        if (n.IsNestedLoop && n.Children.Count >= 2)
        {
            var times = ran ? n.Children[1].Loops ?? 0 : analyzed ? 0 : n.Children[0].EstimatedRows;
            if (times >= ManyLoops)
                advice.Add(new PlanAdvice("loops", (ran ? $"The inner side ran {Count(times)} times." : $"The inner side would run about {Count(times)} times.")
                    + " With this many rows a hash or merge join is usually faster: check the row estimates below."));
        }
        return advice;
    }

    private static string Count(double v) => Math.Round(v).ToString("N0", Inv);

    /// <summary>A table (or CTE, or derived table) as the statement writes it, and the name a plan calls it by.</summary>
    private sealed record TableRef(string Name, string? Relation, PlanSqlRange Range)
    {
        public bool Matches(PlanNode n) =>
            string.Equals(Name, n.Alias, StringComparison.OrdinalIgnoreCase) &&
            (Relation is null || n.Relation is null || string.Equals(Relation, n.Relation, StringComparison.OrdinalIgnoreCase));
    }

    private static List<TableRef> TableRefs(ISqlParser parser, IIdentifierPolicy policy, DatabaseSchema? schema, string source, int offset)
    {
        var tokens = parser.Tokenize(source).ToList();
        var scope = ScopeAnalyzer.Analyze(schema ?? new DatabaseSchema([]), policy, tokens, source);
        var refs = new List<TableRef>();
        foreach (var e in scope.Entries)
        {
            var first = e.SchemaTok >= 0 ? e.SchemaTok : e.NameTok >= 0 ? e.NameTok : e.AliasTok;
            var last = e.AliasTok >= 0 ? e.AliasTok : e.NameTok;
            if (first < 0 || last < 0) continue;
            var start = tokens[first].Start;
            refs.Add(new TableRef((e.AliasId ?? e.NameId).Name, e.Rel?.Name ?? (e.NameTok >= 0 ? e.NameId.Name : null),
                new PlanSqlRange(offset + start, tokens[last].End - start)));
        }
        return refs;
    }
}
