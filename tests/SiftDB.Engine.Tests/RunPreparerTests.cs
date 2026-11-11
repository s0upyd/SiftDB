using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

public class RunPreparerTests
{
    // "select * from Person where PersonId = 1": corrections at Person (14) and PersonId (27).
    private const string Text = "select * from Person where PersonId = 1";

    private static Correction QuoteAt(string word, string quoted) =>
        new(Text.IndexOf(word, StringComparison.Ordinal), word.Length, quoted, $"{word} must be quoted");

    private static readonly ResolverResult TwoCorrections = new(
        [QuoteAt("Person", "\"Person\""), QuoteAt("PersonId", "\"PersonId\"")], []);

    private static readonly SqlRange Whole = new(0, Text.Length);

    [Fact]
    public void Assisted_applies_the_corrections_so_what_runs_is_what_is_shown()
    {
        var run = RunPreparer.Prepare(Text, Whole, TwoCorrections, QueryMode.Assisted);
        Assert.Equal("select * from \"Person\" where \"PersonId\" = 1", run.Sql);
        Assert.Null(run.Blocked);
    }

    [Fact]
    public void Strict_runs_the_text_as_written_and_blocks_while_problems_remain()
    {
        var run = RunPreparer.Prepare(Text, Whole, TwoCorrections, QueryMode.Strict);
        Assert.Equal(Text, run.Sql);
        Assert.NotNull(run.Blocked);
        Assert.Contains("2 identifier problems", run.Blocked);
    }

    [Fact]
    public void Strict_with_nothing_to_fix_just_runs()
    {
        var run = RunPreparer.Prepare(Text, Whole, ResolverResult.Empty, QueryMode.Strict);
        Assert.Equal(Text, run.Sql);
        Assert.Null(run.Blocked);
    }

    [Fact]
    public void Offsets_are_relative_to_the_range_being_run()
    {
        var sheet = "select 1;\n" + Text;
        var shift = "select 1;\n".Length;
        var resolved = new ResolverResult([new Correction(shift + 14, 6, "\"Person\"", "m")], []);
        var run = RunPreparer.Prepare(sheet, new SqlRange(shift, Text.Length), resolved, QueryMode.Assisted);
        Assert.Equal("select * from \"Person\" where PersonId = 1", run.Sql);
    }

    [Fact]
    public void Corrections_outside_the_range_are_ignored()
    {
        var sheet = Text + ";\n" + Text;
        var second = Text.Length + 2;
        var resolved = new ResolverResult(
            [new Correction(14, 6, "\"Person\"", "first"), new Correction(second + 14, 6, "\"Person\"", "second")], []);

        var run = RunPreparer.Prepare(sheet, new SqlRange(second, Text.Length), resolved, QueryMode.Assisted);
        Assert.Equal("select * from \"Person\" where PersonId = 1", run.Sql);
    }

    [Fact]
    public void A_selection_across_statements_gets_every_correction_inside_it()
    {
        var sheet = Text + ";\n" + Text;
        var second = Text.Length + 2;
        var resolved = new ResolverResult(
            [new Correction(14, 6, "\"Person\"", "a"), new Correction(second + 14, 6, "\"Person\"", "b")], []);

        var run = RunPreparer.Prepare(sheet, new SqlRange(0, sheet.Length), resolved, QueryMode.Assisted);
        Assert.Equal(2, run.Sql.Split("\"Person\"").Length - 1);
    }

    [Fact]
    public void An_ambiguity_blocks_even_in_assisted_mode_and_nothing_is_filled_in()
    {
        var resolved = new ResolverResult(
            [QuoteAt("Person", "\"Person\"")],
            [new Issue(27, 8, IssueCode.Ambiguous, IssueSeverity.Error, "PersonId could mean p.\"PersonId\" or e.\"PersonId\"", null)]);

        var run = RunPreparer.Prepare(Text, Whole, resolved, QueryMode.Assisted);
        Assert.Equal(Text, run.Sql);
        Assert.Contains("could mean", run.Blocked);
    }

    [Fact]
    public void A_case_mismatch_is_neither_applied_nor_blocking_in_assisted_mode()
    {
        var resolved = new ResolverResult([],
            [new Issue(14, 6, IssueCode.CaseMismatch, IssueSeverity.Warning, "person differs in case", "\"Person\"")]);

        var run = RunPreparer.Prepare(Text, Whole, resolved, QueryMode.Assisted);
        Assert.Equal(Text, run.Sql);
        Assert.Null(run.Blocked);
    }

    [Fact]
    public void A_case_mismatch_does_block_in_strict_mode()
    {
        var resolved = new ResolverResult([],
            [new Issue(14, 6, IssueCode.CaseMismatch, IssueSeverity.Warning, "person differs in case", "\"Person\"")]);

        Assert.NotNull(RunPreparer.Prepare(Text, Whole, resolved, QueryMode.Strict).Blocked);
    }

    [Theory]
    [InlineData("strict", QueryMode.Strict)]
    [InlineData("STRICT", QueryMode.Strict)]
    [InlineData("assisted", QueryMode.Assisted)]
    [InlineData(null, QueryMode.Assisted)]
    [InlineData("nonsense", QueryMode.Assisted)]
    public void The_mode_defaults_to_assisted(string? text, QueryMode expected) =>
        Assert.Equal(expected, RunPreparer.ParseMode(text));
}
