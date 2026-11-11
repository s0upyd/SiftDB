using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

public class IdentifierPolicyTests
{
    private readonly IIdentifierPolicy _p = new PostgresProvider().Identifiers;

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("order_items", "order_items")]
    [InlineData("_tmp1", "_tmp1")]
    [InlineData("col$1", "col$1")]
    public void Plain_lower_case_names_stay_bare(string name, string expected) => Assert.Equal(expected, _p.Quote(name));

    [Theory]
    [InlineData("Person", "\"Person\"")]          // upper case would fold to "person"
    [InlineData("OrderItems", "\"OrderItems\"")]
    [InlineData("my col", "\"my col\"")]
    [InlineData("1st", "\"1st\"")]                // cannot start with a digit
    [InlineData("a-b", "\"a-b\"")]
    [InlineData("é", "\"é\"")]                    // non-ASCII is always quoted
    [InlineData("", "\"\"")]
    public void Names_that_would_not_survive_folding_are_quoted(string name, string expected) => Assert.Equal(expected, _p.Quote(name));

    [Theory]
    [InlineData("PERSON", "person")]
    [InlineData("OrderItems", "orderitems")]
    [InlineData("orders", "orders")]
    [InlineData("ÉCOLE", "École")]   // only ASCII letters fold, as in a UTF-8 Postgres database
    [InlineData("", "")]
    public void Unquoted_names_fold_to_lower_case_ASCII(string name, string expected) => Assert.Equal(expected, _p.Fold(name));

    [Fact]
    public void Embedded_quotes_are_doubled() => Assert.Equal("\"a\"\"b\"", _p.Quote("a\"b"));

    [Theory]
    [InlineData("select")]   // reserved
    [InlineData("user")]     // reserved
    [InlineData("table")]
    [InlineData("order")]
    [InlineData("left")]     // type-or-function-name keyword: not allowed as a column or table name
    [InlineData("join")]
    public void Words_the_grammar_will_not_accept_as_names_are_quoted(string word) =>
        Assert.Equal("\"" + word + "\"", _p.Quote(word));

    [Theory]
    [InlineData("name")]     // unreserved
    [InlineData("type")]
    [InlineData("value")]
    [InlineData("int")]      // column-name keyword: fine as a table or column name
    [InlineData("position")]
    public void Words_the_grammar_accepts_as_names_stay_bare(string word) => Assert.Equal(word, _p.Quote(word));
}
