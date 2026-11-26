using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB.Tests;

/// <summary>The PartiQL lexer, splitter and quoting (design section 14, F19). No database needed.</summary>
public class PartiqlTests
{
    private readonly PartiqlParser _p = new();

    private List<(string Text, SqlTokenKind Kind)> Tokens(string sql) => _p.Tokenize(sql).Select(t => (sql[t.Start..t.End], t.Kind)).ToList();

    [Fact]
    public void Tokens_of_a_select()
    {
        Assert.Equal(
        [
            ("SELECT", SqlTokenKind.Keyword), ("*", SqlTokenKind.Operator), ("FROM", SqlTokenKind.Keyword), ("\"Orders\"", SqlTokenKind.QuotedIdentifier),
            ("WHERE", SqlTokenKind.Keyword), ("pk", SqlTokenKind.Identifier), ("=", SqlTokenKind.Operator), ("'c#42'", SqlTokenKind.String),
            ("AND", SqlTokenKind.Keyword), ("total", SqlTokenKind.Identifier), (">=", SqlTokenKind.Operator), ("1.5e3", SqlTokenKind.Number),
        ], Tokens("SELECT * FROM \"Orders\" WHERE pk = 'c#42' AND total >= 1.5e3"));
    }

    [Fact]
    public void Quotes_inside_strings_and_names_are_doubled()
    {
        Assert.Equal([("'it''s'", SqlTokenKind.String), ("\"a\"\"b\"", SqlTokenKind.QuotedIdentifier)], Tokens("'it''s' \"a\"\"b\""));
    }

    [Fact]
    public void Comments_ion_literals_and_parameters()
    {
        Assert.Equal(
            [("-- note", SqlTokenKind.Comment), ("/* x */", SqlTokenKind.Comment), ("`{a: 1}`", SqlTokenKind.String), ("?", SqlTokenKind.Parameter)],
            Tokens("-- note\n/* x */ `{a: 1}` ?"));
    }

    [Theory]
    [InlineData("select 'open")]
    [InlineData("select \"open")]
    [InlineData("select /* open")]
    public void An_unterminated_token_runs_to_the_end(string sql)
    {
        var last = _p.Tokenize(sql)[^1];
        Assert.Equal(SqlTokenKind.Invalid, last.Kind);
        Assert.Equal(sql.Length, last.End);
    }

    [Fact]
    public void Statements_split_at_semicolons_outside_strings_and_comments()
    {
        const string sql = "select * from a where s = 'x;y'; -- one\n\n; update b set c = 1 -- tail";
        var parts = _p.Split(sql).Select(r => sql.Substring(r.Start, r.Length)).ToList();
        Assert.Equal(["select * from a where s = 'x;y'", "update b set c = 1"], parts);
    }

    [Fact]
    public void The_verb_is_the_first_word()
    {
        Assert.Equal("update", _p.Verb("-- c\n UPDATE x SET a = 1"));
        Assert.Null(_p.Verb("   "));
    }

    [Theory]
    [InlineData("Orders", "Orders")]           // case is kept: DynamoDB names are case-sensitive
    [InlineData("orders_2026", "orders_2026")]
    [InlineData("orders-dev", "\"orders-dev\"")]
    [InlineData("value", "\"value\"")]         // a PartiQL keyword
    [InlineData("2fa", "\"2fa\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    public void Names_are_quoted_only_when_they_must_be(string name, string written) => Assert.Equal(written, new DynamoIdentifiers().Quote(name));

    [Fact]
    public void Names_are_never_folded() => Assert.Equal("Orders", new DynamoIdentifiers().Fold("Orders"));
}
