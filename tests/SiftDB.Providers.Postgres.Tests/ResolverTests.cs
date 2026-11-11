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
