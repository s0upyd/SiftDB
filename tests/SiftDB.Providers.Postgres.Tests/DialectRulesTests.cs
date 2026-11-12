using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>SQL Postgres refuses with a message that does not say why: caught before it runs (design section 3).</summary>
public class DialectRulesTests
{
    private static readonly PostgresProvider Provider = new();

    private static RelationInfo Rel(string name, params (string Name, string Type)[] columns) =>
        new(name, RelationKind.Table, columns.Select(c => new ColumnInfo(c.Name, c.Type, true, null)).ToList(), [], [], []);

    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [
            Rel("Address", ("AddressId", "integer"), ("PersonId", "integer"), ("IsPrimary", "boolean"), ("is_active", "boolean")),
            Rel("shapes", ("id", "integer"), ("pos", "point_type")),   // pos stands in for a composite column
            Rel("a", ("id", "integer")),
        ]),
    ]);

    private static IReadOnlyList<Issue> Issues(string sql, DialectRules? dialect = null) =>
        IdentifierResolver.Resolve(Schema, Provider.Parser, Provider.Identifiers, sql, dialect ?? Provider.Dialect, Provider.GenericTypeOf).Issues;

    private static Issue Single(string sql, IssueCode code) => Assert.Single(Issues(sql), i => i.Code == code);

    // ---- SET a.col ----

    [Theory]
    [InlineData("update \"Address\" a set a.\"IsPrimary\" = false where a.\"PersonId\" = 2", "a.\"IsPrimary\"", "\"IsPrimary\"")]
    [InlineData("update \"Address\" as a set \"PersonId\" = 1, a.\"IsPrimary\" = false", "a.\"IsPrimary\"", "\"IsPrimary\"")]
    [InlineData("update \"Address\" set \"Address\".\"IsPrimary\" = false", "\"Address\".\"IsPrimary\"", "\"IsPrimary\"")]
    [InlineData("update \"Address\" a set a.IsPrimary = false", "a.IsPrimary", "\"IsPrimary\"")]   // the fix is quoted properly
    public void A_qualified_SET_column_is_reported_with_a_fix(string sql, string span, string fix)
    {
        var issue = Single(sql, IssueCode.QualifiedSetColumn);
        Assert.Equal(sql.IndexOf(span, StringComparison.Ordinal), issue.Start);
        Assert.Equal(span.Length, issue.Length);
        Assert.Equal(fix, issue.FixText);
        Assert.Contains($"Write {fix}", issue.Message);
    }

    [Theory]
    [InlineData("update \"Address\" a set \"IsPrimary\" = false where a.\"PersonId\" = 2")]   // the alias in WHERE is fine
    [InlineData("update shapes set pos.x = 1")]                                             // a field of a composite column
    [InlineData("update \"Address\" a set \"IsPrimary\" = (select a.\"IsPrimary\" from \"Address\" x limit 1)")]
    [InlineData("update \"Address\" a set \"PersonId\" = 1 from a b where b.id = a.\"PersonId\"")]
    [InlineData("select a.\"IsPrimary\" from \"Address\" a")]
    public void Valid_SQL_is_left_alone(string sql) =>
        Assert.DoesNotContain(Issues(sql), i => i.Code == IssueCode.QualifiedSetColumn);

    [Fact]
    public void A_dialect_that_allows_it_says_nothing() =>
        Assert.DoesNotContain(Issues("update \"Address\" a set a.\"IsPrimary\" = false", DialectRules.Permissive),
            i => i.Code == IssueCode.QualifiedSetColumn);

    // ---- boolean = 0 / 1 ----

    [Theory]
    [InlineData("update \"Address\" set \"IsPrimary\" = 0 where \"PersonId\" = 2", "0", "false")]
    [InlineData("select * from \"Address\" where \"IsPrimary\" = 1", "1", "true")]
    [InlineData("select * from \"Address\" a where a.is_active <> 0", "0", "false")]
    public void Zero_or_one_for_a_boolean_is_reported_with_false_or_true(string sql, string number, string fix)
    {
        var issue = Single(sql, IssueCode.NumberForBoolean);
        Assert.Equal(sql.LastIndexOf(number, StringComparison.Ordinal), issue.Start);
        Assert.Equal(fix, issue.FixText);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData("select * from \"Address\" where \"IsPrimary\" = true")]
    [InlineData("select * from \"Address\" where \"PersonId\" = 1")]          // an integer column
    [InlineData("select * from \"Address\" where \"IsPrimary\" = 1::boolean")] // cast: valid
    [InlineData("select * from \"Address\" where \"IsPrimary\" = 2")]          // not a boolean mix-up
    public void Valid_or_unrelated_comparisons_are_left_alone(string sql) =>
        Assert.DoesNotContain(Issues(sql), i => i.Code == IssueCode.NumberForBoolean);

    [Fact]
    public void A_dialect_with_integer_booleans_says_nothing() =>
        Assert.DoesNotContain(Issues("select * from \"Address\" where \"IsPrimary\" = 1", DialectRules.Permissive),
            i => i.Code == IssueCode.NumberForBoolean);

    // ---- through the engine ----

    [Theory]
    [InlineData("assisted")]
    [InlineData("strict")]
    public void The_reported_statement_is_stopped_with_a_message_that_says_why(string mode)
    {
        const string sql = "update \"Address\" a set a.\"IsPrimary\" = 0 where a.\"PersonId\" = 2";
        var resolved = IdentifierResolver.Resolve(Schema, Provider.Parser, Provider.Identifiers, sql, Provider.Dialect, Provider.GenericTypeOf);
        var run = RunPreparer.Prepare(sql, new SqlRange(0, sql.Length), resolved, RunPreparer.ParseMode(mode));
        Assert.Contains("cannot be qualified", run.Blocked);
        Assert.Contains(resolved.Issues, i => i.Code == IssueCode.NumberForBoolean);
    }
}
