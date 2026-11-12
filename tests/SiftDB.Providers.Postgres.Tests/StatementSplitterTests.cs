using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Blank lines as statement ends (design section 6), with the real Postgres grammar deciding what is complete.</summary>
public class StatementSplitterTests
{
    private static readonly StatementSplitter Splitter = StatementSplitter.For(new PostgresProvider().Parser);

    private static string[] Split(string sql) => Splitter.Split(sql).Select(r => sql.Substring(r.Start, r.Length)).ToArray();

    [Theory]
    [InlineData("select 1\n\nselect 2", new[] { "select 1", "select 2" })]
    [InlineData("select 1\r\n\r\nselect 2", new[] { "select 1", "select 2" })]
    [InlineData("select 1\n  \t\nselect 2", new[] { "select 1", "select 2" })]
    [InlineData("select 1\n\n\nselect 2\n\nselect 3", new[] { "select 1", "select 2", "select 3" })]
    [InlineData("select 1;\n\nselect 2", new[] { "select 1", "select 2" })]
    [InlineData("select 1\n\n-- the next one\nselect 2", new[] { "select 1", "select 2" })] // comments are never part of a statement
    [InlineData("select 1\n-- a note\n\nselect 2", new[] { "select 1", "select 2" })]
    [InlineData("SELECT 1\n\nUPDATE t SET a = 1", new[] { "SELECT 1", "UPDATE t SET a = 1" })]
    [InlineData("select * from t\n\nselect * from", new[] { "select * from t", "select * from" })] // half-typed below
    public void A_blank_line_ends_a_complete_statement_when_a_new_one_starts(string sql, string[] expected) =>
        Assert.Equal(expected, Split(sql));

    [Fact]
    public void The_reported_case_splits_into_two_updates()
    {
        var parts = Split("update \"Address\" set \"IsPrimary\" = 0 where \"PersonId\" = 2\n\nupdate \"Address\" a set \"IsPrimary\" = False where a.\"PersonId\" = 2");
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("update \"Address\" a", parts[1]);
    }

    [Theory]
    [InlineData("with x as (select 1)\n\nselect * from x")]              // the CTE alone is not a statement
    [InlineData("select a\n\nfrom t")]                                  // FROM does not start a statement
    [InlineData("select *\nfrom t\n\nwhere a = 1\n\norder by a")]
    [InlineData("insert into t (a)\n\nselect 1")]                       // the insert alone is incomplete
    [InlineData("insert into t (a)\n\nvalues (1)")]
    [InlineData("select 1\n\nunion all\n\nselect 2")]
    [InlineData("create view v as\n\nselect 1")]
    [InlineData("select 'a\n\nselect 2'")]                              // inside a string
    [InlineData("select 1 /*\n\nselect 2\n\n*/")]                       // inside a comment
    [InlineData("do $$\nbegin\n\n  perform 1;\n\nend $$")]              // inside a body
    [InlineData("create function f() returns int language sql\nbegin atomic\n\nselect 1;\n\nend")]
    [InlineData("select (\n\nselect 1)")]
    public void A_blank_line_inside_a_statement_keeps_it_whole(string sql) => Assert.Single(Split(sql));

    [Fact]
    public void An_empty_line_inside_a_block_comment_between_statements_is_not_a_blank_line() =>
        Assert.Single(Split("select 1\n/*\n\n*/\nselect 2"));

    [Theory]
    [InlineData("select 1\nselect 2", 8, 9)]
    [InlineData("update t set a = 1\n-- next\nupdate t set a = 2", 18, 27)]
    public void A_missing_semicolon_between_lines_is_found(string sql, int aboveEnd, int nextStart)
    {
        var range = Splitter.Split(sql).Single();
        Assert.Equal((aboveEnd, nextStart), Splitter.MissingSemicolon(sql, range));
    }

    [Theory]
    [InlineData("select 1\nfrom t")]            // valid: nothing is missing
    [InlineData("select 1 select 2")]           // one line: not this mistake
    [InlineData("select a\nfrom t where")]      // broken, but not two statements
    public void Nothing_is_reported_when_it_is_not_two_statements(string sql)
    {
        var range = Splitter.Split(sql).Single();
        Assert.Null(Splitter.MissingSemicolon(sql, range));
    }
}
