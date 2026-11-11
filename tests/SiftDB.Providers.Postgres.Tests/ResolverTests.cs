using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>What the identifier resolver says about a statement, with the real tokenizer and quoting rules.</summary>
public class ResolverTests
{
    private static readonly PostgresProvider Provider = new();

    private static RelationInfo Rel(string name, params string[] columns) =>
        new(name, RelationKind.Table, columns.Select(c => new ColumnInfo(c, "text", true, null)).ToList(), [], [], []);

    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [
            Rel("Person", "PersonId", "FirstName", "LastName"),
            Rel("Email", "EmailId", "PersonId", "EmailAddress"),
            Rel("orders", "id", "customer_id", "user", "total"),
            Rel("user", "id"),
        ]),
        new SchemaInfo("shop", [Rel("products", "sku", "name")]),
        new SchemaInfo("Sales", [Rel("Orders", "OrderId")]),
    ]);

    private static ResolverResult Resolve(string sql) =>
        IdentifierResolver.Resolve(Schema, Provider.Parser, Provider.Identifiers, sql);

    /// <summary>The text with every automatic correction applied (last first, so offsets stay valid).</summary>
    private static string Fixed(string sql)
    {
        var result = sql;
        foreach (var c in Resolve(sql).Corrections.OrderByDescending(c => c.Start))
            result = result.Remove(c.Start, c.Length).Insert(c.Start, c.Text);
        return result;
    }

    // ---- tables ----

    [Theory]
    [InlineData("select * from orders")]
    [InlineData("select * from ORDERS")]                 // Postgres folds it to orders: already right
    [InlineData("select * from Orders")]
    [InlineData("select * from \"Person\"")]
    [InlineData("select * from public.orders")]
    [InlineData("select * from shop.Products")]          // folds to products
    public void Leaves_valid_SQL_alone(string sql)
    {
        var r = Resolve(sql);
        Assert.Empty(r.Corrections);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Adds_the_quotes_a_mixed_case_table_needs()
    {
        Assert.Equal("select * from \"Person\"", Fixed("select * from Person"));
    }

    [Fact]
    public void A_correction_is_a_pure_insertion_of_quotes()
    {
        var c = Assert.Single(Resolve("select * from Person").Corrections);
        Assert.Equal("Person", "select * from Person".Substring(c.Start, c.Length));
        Assert.Equal("\"Person\"", c.Text);
        Assert.Contains("quoted", c.Message);
    }

    [Fact]
    public void A_reserved_word_used_as_a_table_name_gets_quoted()
    {
        Assert.Equal("select * from \"user\"", Fixed("select * from user"));
    }

    [Fact]
    public void Schema_and_table_are_both_quoted_when_both_need_it()
    {
        Assert.Equal("select * from \"Sales\".\"Orders\"", Fixed("select * from Sales.Orders"));
    }

    [Fact]
    public void A_difference_in_case_only_is_offered_as_a_fix_not_applied()
    {
        var r = Resolve("select * from person");
        Assert.Empty(r.Corrections);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(IssueCode.CaseMismatch, issue.Code);
        Assert.Equal("\"Person\"", issue.FixText);
    }

    [Fact]
    public void A_quoted_name_with_the_wrong_case_is_a_case_mismatch()
    {
        var issue = Assert.Single(Resolve("select * from \"person\"").Issues);
        Assert.Equal(IssueCode.CaseMismatch, issue.Code);
        Assert.Equal("\"Person\"", issue.FixText);
    }

    [Fact]
    public void Tables_that_do_not_exist_are_not_commented_on()
    {
        var r = Resolve("select Foo from Bar");
        Assert.Empty(r.Corrections);
        Assert.Empty(r.Issues);
    }

    // ---- columns ----

    [Fact]
    public void Qualified_columns_are_quoted()
    {
        Assert.Equal("select p.\"PersonId\" from \"Person\" p", Fixed("select p.PersonId from \"Person\" p"));
    }

    [Fact]
    public void Bare_columns_are_found_in_the_tables_in_scope()
    {
        Assert.Equal("select \"PersonId\", \"FirstName\" from \"Person\"", Fixed("select PersonId, FirstName from \"Person\""));
    }

    [Fact]
    public void Columns_are_resolved_in_every_clause()
    {
        Assert.Equal("select \"FirstName\" from \"Person\" where \"LastName\" = 'x' order by \"FirstName\"",
            Fixed("select FirstName from \"Person\" where LastName = 'x' order by FirstName"));
    }

    [Fact]
    public void A_whole_join_is_fixed_in_one_pass()
    {
        Assert.Equal(
            "select p.\"FirstName\", e.\"EmailAddress\" from \"Person\" p join \"Email\" e on e.\"PersonId\" = p.\"PersonId\"",
            Fixed("select p.FirstName, e.EmailAddress from Person p join Email e on e.PersonId = p.PersonId"));
    }

    [Fact]
    public void A_column_in_one_table_only_is_not_ambiguous()
    {
        Assert.Equal("select \"FirstName\" from \"Person\" p, \"Email\" e", Fixed("select FirstName from \"Person\" p, \"Email\" e"));
    }

    [Fact]
    public void A_column_two_tables_share_is_ambiguous_and_nothing_is_filled_in()
    {
        const string sql = "select PersonId from \"Person\" p, \"Email\" e";
        var r = Resolve(sql);
        Assert.Empty(r.Corrections.Where(c => sql.Substring(c.Start, c.Length) == "PersonId"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal(IssueCode.Ambiguous, issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Null(issue.FixText);
        Assert.Contains("p.\"PersonId\"", issue.Message);
        Assert.Contains("e.\"PersonId\"", issue.Message);
    }

    [Fact]
    public void A_difference_in_case_only_on_a_column_is_a_suggestion()
    {
        var issue = Assert.Single(Resolve("select personid from \"Person\"").Issues);
        Assert.Equal(IssueCode.CaseMismatch, issue.Code);
        Assert.Equal("\"PersonId\"", issue.FixText);
    }

    [Fact]
    public void A_reserved_word_is_fine_after_a_dot_but_not_bare()
    {
        Assert.Empty(Resolve("select o.user from orders o").Corrections);   // o.user is valid Postgres
        Assert.Empty(Resolve("select * from public.user").Corrections);
    }

    [Fact]
    public void Insert_column_lists_are_resolved()
    {
        Assert.Equal("insert into \"Person\" (\"FirstName\", \"LastName\") values ('a', 'b')",
            Fixed("insert into Person (FirstName, LastName) values ('a', 'b')"));
    }

    [Fact]
    public void Update_assignments_are_resolved()
    {
        Assert.Equal("update \"Person\" set \"FirstName\" = 'x' where \"PersonId\" = 1",
            Fixed("update Person set FirstName = 'x' where PersonId = 1"));
    }

    [Fact]
    public void Aliases_match_however_they_are_written()
    {
        // P and p are the same alias to Postgres (unquoted names fold).
        Assert.Equal("select P.\"PersonId\" from \"Person\" p", Fixed("select P.PersonId from \"Person\" p"));
    }

    // ---- things that must never be touched ----

    [Fact]
    public void Strings_and_comments_are_never_rewritten()
    {
        var r = Resolve("select 'Person', 'FirstName' from \"Person\" -- Person, FirstName\n/* PersonId */ where 1 = 1");
        Assert.Empty(r.Corrections);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Function_names_are_not_columns()
    {
        Assert.Equal("select Count(*), Max(\"PersonId\") from \"Person\"", Fixed("select Count(*), Max(PersonId) from \"Person\""));
    }

    [Fact]
    public void Type_names_after_a_cast_are_left_alone()
    {
        Assert.Empty(Resolve("select \"PersonId\"::text from \"Person\"").Corrections);
    }

    [Fact]
    public void Output_aliases_are_not_treated_as_columns()
    {
        var r = Resolve("select \"FirstName\" as firstname from \"Person\" order by firstname");
        Assert.Empty(r.Corrections);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Derived_tables_hide_what_we_cannot_know()
    {
        var r = Resolve("select x.Foo from (select 1 as Foo) x");
        Assert.Empty(r.Corrections);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Statements_that_are_not_queries_are_left_alone()
    {
        Assert.Empty(Resolve("create table Person (PersonId int)").Corrections);
        Assert.Empty(Resolve("drop table if exists Person").Corrections.Where(c => false)); // no crash
    }

    // ---- quote mix-ups ----

    [Fact]
    public void A_double_quoted_word_that_is_no_column_is_probably_a_string()
    {
        const string sql = "select * from shop.products where name = \"bob\"";
        var issue = Assert.Single(Resolve(sql).Issues);
        Assert.Equal(IssueCode.StringQuote, issue.Code);
        Assert.Equal("'bob'", issue.FixText);
        Assert.Equal("\"bob\"", sql.Substring(issue.Start, issue.Length));
    }

    [Fact]
    public void A_double_quoted_word_that_is_a_column_is_fine()
    {
        Assert.Empty(Resolve("select * from shop.products where name = \"sku\"").Issues);
    }

    [Fact]
    public void Quotes_inside_the_word_are_escaped_in_the_suggested_string()
    {
        var issue = Assert.Single(Resolve("select * from shop.products where name = \"it's\"").Issues);
        Assert.Equal("'it''s'", issue.FixText);
    }

    [Fact]
    public void No_string_suggestion_when_a_derived_table_might_own_the_column()
    {
        Assert.Empty(Resolve("select * from shop.products p, (select 1 as bob) x where name = \"bob\"").Issues);
    }

    // ---- columns that do not exist ----

    [Theory]
    [InlineData("select * from \"Person\" where id = 1")]
    [InlineData("select * from \"Person\" where Id = 1")]
    [InlineData("select * from \"Person\" where ID = 1")]
    public void A_column_that_does_not_exist_gets_a_hint_naming_the_likely_one(string sql)
    {
        var r = Resolve(sql);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(IssueCode.UnknownColumn, issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal("\"PersonId\"", issue.FixText);                // "id" is the end of "PersonId"
        Assert.Contains("No column", issue.Message);
        Assert.Contains("\"Person\"", issue.Message);
        Assert.Contains("\"PersonId\"", issue.Message);
        Assert.Equal("id", sql.Substring(issue.Start, issue.Length).ToLowerInvariant());
        Assert.Empty(r.Corrections);                                 // a guess is never applied silently
    }

    [Fact]
    public void An_alias_qualified_column_that_does_not_exist_gets_the_same_hint()
    {
        var issue = Assert.Single(Resolve("select p.id from \"Person\" p").Issues);
        Assert.Equal(IssueCode.UnknownColumn, issue.Code);
        Assert.Equal("\"PersonId\"", issue.FixText);
    }

    [Theory]
    [InlineData("firstnme", "\"FirstName\"")]   // a typo
    [InlineData("firstn", "\"FirstName\"")]     // a prefix
    [InlineData("stnam", "\"FirstName\"")]      // something inside it
    public void The_hint_finds_typos_prefixes_and_fragments(string typed, string expected)
    {
        var issue = Assert.Single(Resolve($"select {typed} from \"Person\"").Issues);
        Assert.Equal(expected, issue.FixText);
    }

    [Fact]
    public void With_nothing_close_the_hint_still_says_the_column_is_missing()
    {
        var issue = Assert.Single(Resolve("select zzzzzz from \"Person\"").Issues);
        Assert.Equal(IssueCode.UnknownColumn, issue.Code);
        Assert.Null(issue.FixText);
        Assert.Empty(issue.AllFixes);
        Assert.Equal("No column zzzzzz in \"Person\".", issue.Message);
    }

    [Fact]
    public void Candidates_are_offered_best_first_and_a_shared_name_is_qualified()
    {
        var issue = Assert.Single(Resolve("select id from \"Person\" p, \"Email\" e").Issues);
        Assert.Equal(["p.\"PersonId\"", "\"EmailId\"", "e.\"PersonId\""], issue.AllFixes);
        Assert.Contains("\"Person\" or \"Email\"", issue.Message);
    }

    [Fact]
    public void At_most_three_candidates_are_offered()
    {
        var wide = new DatabaseSchema([new SchemaInfo("public", [Rel("t", "a_id", "b_id", "c_id", "d_id", "e_id")])]);
        var r = IdentifierResolver.Resolve(wide, Provider.Parser, Provider.Identifiers, "select id from t");
        Assert.Equal(3, Assert.Single(r.Issues).AllFixes.Count);
    }

    [Theory]
    [InlineData("select p from \"Person\" p")]                                                   // the whole row
    [InlineData("select \"Person\" from \"Person\"")]
    [InlineData("select \"FirstName\" fn from \"Person\"")]                                      // alias without AS
    [InlineData("select \"FirstName\" fn from \"Person\" order by fn")]
    [InlineData("select \"FirstName\" as fn from \"Person\" order by fn")]
    [InlineData("select case when \"PersonId\" > 1 then 1 end flag from \"Person\"")]            // alias after END
    [InlineData("select count(*) c from \"Person\"")]
    [InlineData("select * from \"Person\" order by idx")]                                        // may be an output column
    [InlineData("select extract(epoch from \"FirstName\") from \"Person\"")]
    [InlineData("select substring(\"FirstName\" from 2) from \"Person\"")]
    [InlineData("select now(), lower(\"FirstName\") from \"Person\"")]
    [InlineData("select f(width => 3) from \"Person\"")]
    [InlineData("select * from \"Person\" where \"PersonId\" = 1")]
    [InlineData("select * from \"Person\" where 1 = 1 and \"FirstName\" like 'a%'")]
    public void Valid_SQL_is_never_flagged_as_having_an_unknown_column(string sql)
    {
        var r = Resolve(sql);
        Assert.Empty(r.Issues);
        Assert.Empty(r.Corrections);
    }

    [Theory]
    [InlineData("select x from (select 1 as x) s, \"Person\" p")]                               // a derived table may own it
    [InlineData("with c as (select 1 as v) select v from c, \"Person\" p")]                       // so may a CTE
    [InlineData("select v from generate_series(1, 3) v, \"Person\" p")]                         // so may a function
    public void Nothing_is_claimed_when_part_of_the_statement_is_unknown(string sql)
    {
        Assert.Empty(Resolve(sql).Issues);
    }

    [Fact]
    public void A_qualified_unknown_column_is_flagged_even_in_order_by()
    {
        var issue = Assert.Single(Resolve("select * from \"Person\" p order by p.idx").Issues);
        Assert.Equal(IssueCode.UnknownColumn, issue.Code);
    }

    [Fact]
    public void A_double_quoted_string_in_a_value_position_is_still_a_string_hint_not_an_unknown_column()
    {
        var issue = Assert.Single(Resolve("select * from shop.products where name = \"bob\"").Issues);
        Assert.Equal(IssueCode.StringQuote, issue.Code);
    }

    [Fact]
    public void An_unknown_column_does_not_stop_the_real_corrections_around_it()
    {
        const string sql = "select FirstName from \"Person\" where id = 1";
        Assert.Equal("select \"FirstName\" from \"Person\" where id = 1", Fixed(sql));   // FirstName is fixed; id is only hinted at
        Assert.Equal(IssueCode.UnknownColumn, Assert.Single(Resolve(sql).Issues).Code);
    }

    // ---- positions and shape ----

    [Fact]
    public void Offsets_are_utf16_after_non_ascii_text()
    {
        Assert.Equal("select 'é', \"FirstName\" from \"Person\"", Fixed("select 'é', FirstName from Person"));
    }

    [Fact]
    public void Every_statement_in_a_sheet_is_resolved()
    {
        Assert.Equal("select * from \"Person\";\nselect * from \"Email\";", Fixed("select * from Person;\nselect * from Email;"));
    }

    [Fact]
    public void Half_typed_SQL_does_not_throw_and_still_fixes_what_it_can()
    {
        Assert.Equal("select * from \"Person\" where", Fixed("select * from Person where"));
        Assert.Equal("select * from \"Person\" p where p.", Fixed("select * from Person p where p."));
        Assert.Equal("select * from \"Person\" where x = 'unterminated", Fixed("select * from Person where x = 'unterminated"));
    }

    [Fact]
    public void An_empty_schema_or_text_gives_nothing()
    {
        Assert.Same(ResolverResult.Empty, ResolverResult.Empty);
        Assert.Empty(IdentifierResolver.Resolve(new DatabaseSchema([]), Provider.Parser, Provider.Identifiers, "select * from Person").Corrections);
        Assert.Empty(Resolve("").Corrections);
    }
}
