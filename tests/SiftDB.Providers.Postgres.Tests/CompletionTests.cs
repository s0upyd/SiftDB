using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>Suggestions on half-typed SQL, with the real tokenizer and quoting rules. '|' marks the cursor.</summary>
public class CompletionTests
{
    private static readonly PostgresProvider Provider = new();

    private static RelationInfo Rel(string name, string[] columns, string[]? pk = null, RelationKind kind = RelationKind.Table) =>
        new(name, kind, columns.Select(c => new ColumnInfo(c, "text", true, null)).ToList(), pk ?? [], [], []);

    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [
            Rel("Person", ["PersonId", "FirstName", "LastName"], ["PersonId"]),
            Rel("Email", ["EmailId", "PersonId", "EmailAddress"], ["EmailId"]),
            Rel("orders", ["id", "customer_id", "user", "total"]),
            Rel("v_people", ["PersonId", "FirstName"], kind: RelationKind.View),
        ]),
        new SchemaInfo("shop", [Rel("products", ["sku", "name"])]),
        new SchemaInfo("empty", []),
    ]);

    private static (CompletionResult Result, string Text, int Offset) Run(string marked, DatabaseSchema? schema = null)
    {
        var offset = marked.IndexOf('|');
        var text = marked.Remove(offset, 1);
        var r = CompletionEngine.Complete(schema ?? Schema, Provider.Parser, Provider.Identifiers, text, offset);
        return (r, text, offset);
    }

    private static string[] Labels(string marked) => Run(marked).Result.Items.Select(i => i.Label).ToArray();
    private static CompletionItem Item(string marked, string label) => Run(marked).Result.Items.Single(i => i.Label == label);

    // ---- tables ----

    [Theory]
    [InlineData("select * from |")]
    [InlineData("select * from \n  |")]
    [InlineData("select * from \"Person\" p join |")]
    [InlineData("select * from \"Person\" p left join |")]
    [InlineData("select * from \"Person\" p, |")]
    [InlineData("insert into |")]
    [InlineData("update |")]
    [InlineData("delete from |")]
    [InlineData("select 1 from orders where id in (select customer_id from |")]
    public void Offers_tables_where_a_table_goes(string marked)
    {
        var labels = Labels(marked);
        Assert.Contains("Person", labels);
        Assert.Contains("orders", labels);
        Assert.Contains("v_people", labels);
    }

    [Fact]
    public void Quotes_table_names_only_where_needed()
    {
        Assert.Equal("\"Person\"", Item("select * from |", "Person").InsertText);
        Assert.Equal("orders", Item("select * from |", "orders").InsertText);
    }

    [Fact]
    public void Tables_outside_public_are_inserted_with_their_schema_and_schemas_are_offered()
    {
        Assert.Equal("shop.products", Item("select * from |", "products").InsertText);
        var shop = Item("select * from |", "shop");
        Assert.Equal(CompletionKind.Schema, shop.Kind);
        Assert.Equal("shop.", shop.InsertText);
    }

    [Fact]
    public void Tables_and_views_have_their_own_kinds()
    {
        Assert.Equal(CompletionKind.Table, Item("select * from |", "Person").Kind);
        Assert.Equal(CompletionKind.View, Item("select * from |", "v_people").Kind);
    }

    [Fact]
    public void A_typed_prefix_filters_and_the_range_covers_what_was_typed()
    {
        var (r, text, offset) = Run("select * from per|");
        Assert.Equal(["Person"], r.Items.Select(i => i.Label));
        Assert.Equal(offset - 3, r.Start);
        Assert.Equal(offset, r.End);
        Assert.Equal("per", text[r.Start..r.End]);
    }

    [Fact]
    public void The_prefix_matches_anywhere_in_the_name_with_prefix_matches_first()
    {
        var labels = Labels("select * from or|");
        Assert.Equal("orders", labels[0]);
    }

    [Fact]
    public void An_unterminated_quote_still_completes_and_the_range_includes_the_quote()
    {
        var (r, text, offset) = Run("select * from \"Pe|");
        var person = r.Items.Single(i => i.Label == "Person"); // v_people matches too: "Pe" is inside "people"
        Assert.Equal("Person", person.Label);
        Assert.Equal("\"Person\"", person.InsertText);
        Assert.Equal("\"Person\"", person.FilterText); // matches the quoted word Monaco sees in the range
        Assert.Equal("\"Pe", text[r.Start..r.End]);
        Assert.Equal(offset, r.End);
    }

    [Fact]
    public void An_unterminated_quote_does_not_swallow_text_after_the_cursor()
    {
        var (r, text, _) = Run("select * from \"Pe|rson where x = 1");
        Assert.Equal("\"Pe", text[r.Start..r.End]);
    }

    [Fact]
    public void Completing_in_the_middle_of_a_word_replaces_the_whole_word()
    {
        var (r, text, _) = Run("select * from Per|son where 1 = 1");
        Assert.Equal("Person", text[r.Start..r.End]);
        Assert.Contains(r.Items, i => i.Label == "Person");
    }

    [Fact]
    public void After_a_schema_dot_it_offers_that_schemas_tables_unqualified()
    {
        var r = Run("select * from shop.|").Result;
        Assert.Equal(["products"], r.Items.Select(i => i.Label));
        Assert.Equal("products", r.Items[0].InsertText);
    }

    // ---- columns ----

    [Fact]
    public void After_an_alias_dot_it_offers_that_tables_columns_quoted_as_needed()
    {
        var r = Run("select * from \"Person\" p where p.|").Result;
        Assert.Equal(["PersonId", "FirstName", "LastName"], r.Items.Select(i => i.Label));
        Assert.Equal("\"PersonId\"", r.Items[0].InsertText);
        Assert.All(r.Items, i => Assert.Equal(CompletionKind.Column, i.Kind));
    }

    [Fact]
    public void The_alias_may_be_defined_after_the_cursor()
    {
        Assert.Equal(["PersonId", "FirstName", "LastName"], Labels("select p.| from \"Person\" p"));
    }

    [Fact]
    public void Each_alias_gets_its_own_columns()
    {
        const string joined = "select * from \"Person\" p join \"Email\" e on ";
        Assert.Equal(["EmailId", "PersonId", "EmailAddress"], Labels(joined + "e.|"));
        Assert.Equal(["PersonId", "FirstName", "LastName"], Labels(joined + "p.|"));
    }

    [Fact]
    public void A_table_without_an_alias_is_reachable_by_its_name()
    {
        Assert.Equal(["PersonId", "FirstName", "LastName"], Labels("select \"Person\".| from \"Person\""));
    }

    [Fact]
    public void An_aliased_table_is_not_reachable_by_its_own_name()
    {
        Assert.Empty(Labels("select \"Person\".| from \"Person\" p"));
    }

    [Fact]
    public void Schema_qualified_table_then_dot_offers_columns()
    {
        Assert.Equal(["sku", "name"], Labels("select shop.products.| from shop.products"));
    }

    [Fact]
    public void Tables_are_matched_ignoring_case_like_the_resolver_does()
    {
        Assert.Equal(["PersonId", "FirstName", "LastName"], Labels("select * from person p where p.|"));
    }

    [Theory]
    [InlineData("select | from \"Person\" p")]
    [InlineData("select p.\"PersonId\", | from \"Person\" p")]
    [InlineData("select * from \"Person\" p where |")]
    [InlineData("select * from \"Person\" p where p.\"PersonId\" = 1 and |")]
    [InlineData("select * from \"Person\" p where p.\"PersonId\" = |")]
    [InlineData("select * from \"Person\" p order by |")]
    [InlineData("select * from \"Person\" p group by |")]
    [InlineData("update \"Person\" set |")]
    [InlineData("insert into \"Person\" (|")]
    [InlineData("insert into \"Person\" (\"FirstName\", |")]
    public void Offers_columns_where_an_expression_goes(string marked)
    {
        Assert.Contains("FirstName", Labels(marked));
    }

    [Fact]
    public void Columns_that_need_quotes_get_them()
    {
        var r = Run("select o.| from orders o").Result;
        Assert.Equal("id", r.Items.Single(i => i.Label == "id").InsertText);
        Assert.Equal("\"user\"", r.Items.Single(i => i.Label == "user").InsertText); // reserved word
    }

    [Fact]
    public void Ambiguous_columns_are_qualified_and_unique_ones_are_not()
    {
        var items = Run("select | from \"Person\" p, \"Email\" e").Result.Items;
        var personIds = items.Where(i => i.Label == "PersonId").Select(i => i.InsertText).OrderBy(x => x).ToArray();
        Assert.Equal(["e.\"PersonId\"", "p.\"PersonId\""], personIds);
        Assert.Equal("\"FirstName\"", items.Single(i => i.Label == "FirstName").InsertText);
        Assert.Equal("\"EmailAddress\"", items.Single(i => i.Label == "EmailAddress").InsertText);
    }

    [Fact]
    public void Aliases_are_offered_too_so_you_can_type_alias_dot()
    {
        var alias = Item("select * from \"Person\" p where |", "p");
        Assert.Equal(CompletionKind.Alias, alias.Kind);
        Assert.Equal("p", alias.InsertText);
    }

    [Fact]
    public void Primary_key_and_type_show_in_the_detail()
    {
        var d = Item("select p.| from \"Person\" p", "PersonId").Detail;
        Assert.Contains("PK", d);
        Assert.Contains("text", d);
    }

    [Fact]
    public void Columns_keep_table_order()
    {
        var items = Run("select p.| from \"Person\" p").Result.Items;
        Assert.Equal(["PersonId", "FirstName", "LastName"], items.OrderBy(i => i.SortText, StringComparer.Ordinal).Select(i => i.Label));
    }

    [Fact]
    public void Only_the_statement_under_the_cursor_counts()
    {
        var labels = Labels("select * from \"Person\"; select * from orders o where o.|");
        Assert.Equal(["id", "customer_id", "user", "total"], labels);
    }

    [Fact]
    public void A_derived_table_in_scope_has_no_known_columns_so_nothing_is_guessed()
    {
        Assert.Empty(Labels("select x.| from (select 1 as a) x"));
    }

    // ---- nothing to offer ----

    [Theory]
    [InlineData("select * from \"Person\" |")]                       // alias position
    [InlineData("select * from \"Person\" p|")]                      // typing an alias
    [InlineData("select a as |")]
    [InlineData("select * |")]
    [InlineData("select x.| from \"Person\" p")]                     // unknown alias
    [InlineData("select 'abc|")]                                      // inside a string
    [InlineData("select * from \"Person\" where name = 'ab|c'")]
    [InlineData("-- select * from |")]                                // inside a comment
    [InlineData("select /* from | */ 1")]
    [InlineData("select 1;\n|")]                                      // fresh statement
    [InlineData("|")]
    [InlineData("select * from \"Person\" for update |")]
    public void Offers_nothing_where_nothing_fits(string marked)
    {
        Assert.Empty(Run(marked).Result.Items);
    }

    [Fact]
    public void Columns_need_a_table_in_scope()
    {
        Assert.Empty(Labels("select |"));
        Assert.Empty(Labels("select * from nosuchtable where |"));
    }

    [Fact]
    public void An_empty_schema_gives_no_tables()
    {
        Assert.Empty(Run("select * from |", new DatabaseSchema([])).Result.Items);
    }

    [Fact]
    public void Out_of_range_offsets_are_clamped()
    {
        var r = CompletionEngine.Complete(Schema, Provider.Parser, Provider.Identifiers, "select * from ", 9999);
        Assert.NotEmpty(r.Items);
        var r2 = CompletionEngine.Complete(Schema, Provider.Parser, Provider.Identifiers, "select * from ", -4);
        Assert.Empty(r2.Items);
    }

    [Fact]
    public void Results_are_capped()
    {
        var many = new DatabaseSchema([new SchemaInfo("public",
            Enumerable.Range(0, 1000).Select(i => Rel("t" + i, ["a"])).ToList())]);
        Assert.Equal(300, Run("select * from |", many).Result.Items.Count);
    }
}
