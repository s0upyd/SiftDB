using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

public class PostgresSqlParserTests
{
    private readonly ISqlParser _p = new PostgresSqlParser();

    private string[] Statements(string sql) => _p.Split(sql).Select(r => sql.Substring(r.Start, r.Length)).ToArray();

    private (SqlTokenKind Kind, string Text)[] Tokens(string sql) =>
        _p.Tokenize(sql).Select(t => (t.Kind, sql[t.Start..t.End])).ToArray();

    // ---- Tokenize ----

    [Fact]
    public void Tokenize_classifies_the_pieces_quoting_cares_about()
    {
        const string sql = "select \"OrderItems\", OrderItems.id from t where n = \"bob\" and s = 'x' and p = $1 -- c";
        var t = Tokens(sql);

        Assert.Contains((SqlTokenKind.Keyword, "select"), t);
        Assert.Contains((SqlTokenKind.QuotedIdentifier, "\"OrderItems\""), t);
        Assert.Contains((SqlTokenKind.Identifier, "OrderItems"), t);
        Assert.Contains((SqlTokenKind.QuotedIdentifier, "\"bob\""), t); // parses fine; the resolver must catch it
        Assert.Contains((SqlTokenKind.String, "'x'"), t);
        Assert.Contains((SqlTokenKind.Parameter, "$1"), t);
        Assert.Contains((SqlTokenKind.Punctuation, ","), t);
        Assert.Contains((SqlTokenKind.Operator, "="), t);
        Assert.Equal((SqlTokenKind.Comment, "-- c"), t[^1]);
    }

    [Fact]
    public void Tokenize_reports_how_reserved_a_keyword_is()
    {
        const string sql = "select name from user";
        var tokens = _p.Tokenize(sql);
        KeywordClass Of(string word) => tokens.Single(t => sql[t.Start..t.End] == word).Keyword;

        Assert.Equal(KeywordClass.Reserved, Of("select"));
        Assert.Equal(KeywordClass.Unreserved, Of("name"));
        Assert.Equal(KeywordClass.Reserved, Of("user")); // a table called "user" must be quoted
    }

    [Theory]
    [InlineData("select 1, 'abc", "'abc")]
    [InlineData("select 1, \"abc", "\"abc")]
    [InlineData("select 1 /* open", "/* open")]
    [InlineData("select 1, $$ never closed", "$$ never closed")]
    public void Tokenize_keeps_earlier_tokens_when_the_tail_is_unterminated(string sql, string badText)
    {
        var t = Tokens(sql);
        Assert.Equal((SqlTokenKind.Invalid, badText), t[^1]);
        Assert.Contains((SqlTokenKind.Keyword, "select"), t);
        Assert.Contains((SqlTokenKind.Number, "1"), t);
        Assert.Single(t, x => x.Kind == SqlTokenKind.Invalid);
    }

    [Fact]
    public void Tokenize_handles_empty_input()
    {
        Assert.Empty(_p.Tokenize(""));
    }

    [Fact]
    public void Tokenize_offsets_are_utf16_not_utf8_bytes()
    {
        const string sql = "select 'ééé', x from t";
        var last = _p.Tokenize(sql)[^1];
        Assert.Equal(sql.LastIndexOf('t'), last.Start);
    }

    [Fact]
    public void Tokenize_tokens_cover_the_text_in_order_without_overlap()
    {
        const string sql = "select a, b -- x\nfrom \"T\" /* y */ where c = 'z' and d = $$q$$";
        var prevEnd = 0;
        foreach (var t in _p.Tokenize(sql))
        {
            Assert.True(t.Start >= prevEnd);
            Assert.True(t.End > t.Start);
            prevEnd = t.End;
        }
    }

    // ---- Split ----

    [Fact]
    public void Split_ignores_semicolons_in_strings_comments_and_dollar_bodies()
    {
        var s = Statements("select 1; select ';' ; /* ; */ select $$a;b$$; select 'x';select 2");
        Assert.Equal(["select 1", "select ';'", "select $$a;b$$", "select 'x'", "select 2"], s);
    }

    [Fact]
    public void Split_ranges_are_trimmed_of_whitespace_comments_and_terminator()
    {
        const string sql = "  -- lead\n  select 1 ;  \n\n /* c */ select 2; -- tail\n";
        Assert.Equal(["select 1", "select 2"], Statements(sql));
    }

    [Fact]
    public void Split_drops_empty_and_comment_only_statements()
    {
        Assert.Equal(["select 1", "select 2"], Statements("select 1;\n-- nothing\n;\n\n;select 2"));
        Assert.Empty(_p.Split("-- just a comment"));
        Assert.Empty(_p.Split("  \n "));
        Assert.Empty(_p.Split(""));
    }

    [Fact]
    public void Split_keeps_function_bodies_whole()
    {
        var s = Statements("create function f() returns int as $$ begin return 1; end $$ language plpgsql; select f();");
        Assert.Equal(2, s.Length);
        Assert.StartsWith("create function", s[0]);
        Assert.EndsWith("language plpgsql", s[0]);
        Assert.Equal("select f()", s[1]);
    }

    [Fact]
    public void Split_keeps_sql_standard_function_bodies_whole()
    {
        var s = Statements("create function g() returns int language sql begin atomic select 1; select 2; end; select 3");
        Assert.Equal(2, s.Length);
        Assert.EndsWith("end", s[0]);
        Assert.Equal("select 3", s[1]);
    }

    [Fact]
    public void Split_handles_half_typed_statements()
    {
        Assert.Equal(["select 1", "select * from orders o where o."], Statements("select 1; select * from orders o where o."));
        Assert.Equal(["select * from", "select 2", "select 3"], Statements("select * from ; select 2; select 3"));
    }

    [Theory]
    [InlineData("select 1; select 2; select 'abc; select 3", new[] { "select 1", "select 2", "select 'abc; select 3" })]
    [InlineData("select 1; select \"abc", new[] { "select 1", "select \"abc" })]
    [InlineData("select 'abc", new[] { "select 'abc" })]
    [InlineData("select 1; /* open", new[] { "select 1" })]
    public void Split_keeps_earlier_statements_when_the_tail_is_unterminated(string sql, string[] expected)
    {
        Assert.Equal(expected, Statements(sql));
    }

    [Theory]
    [InlineData("insert into t (", new[] { "insert into t (" })]
    [InlineData("insert into t (a, ", new[] { "insert into t (a," })]
    [InlineData("select 1; insert into t (a, ", new[] { "select 1", "insert into t (a," })]
    [InlineData("select (1; select 2", new[] { "select (1", "select 2" })]
    [InlineData("select 1 from orders where id in (select customer_id from ", new[] { "select 1 from orders where id in (select customer_id from" })]
    [InlineData("insert into t (a; -- c\nselect 2;", new[] { "insert into t (a", "select 2" })]
    public void Split_does_not_lose_a_statement_with_an_open_parenthesis(string sql, string[] expected)
    {
        Assert.Equal(expected, Statements(sql));
    }

    [Fact]
    public void Split_offsets_are_utf16_after_non_ascii_text()
    {
        const string sql = "select 'éé'; select 'çç'; select 3";
        var ranges = _p.Split(sql);
        Assert.Equal(3, ranges.Count);
        Assert.Equal(sql.IndexOf("select 3", StringComparison.Ordinal), ranges[2].Start);
        Assert.Equal("select 'çç'", sql.Substring(ranges[1].Start, ranges[1].Length));
    }

    [Fact]
    public void Split_ranges_are_in_order_and_do_not_overlap()
    {
        var ranges = _p.Split("select 1; select 2; select 'x");
        for (var i = 1; i < ranges.Count; i++) Assert.True(ranges[i].Start >= ranges[i - 1].End);
    }

    // ---- Validate ----

    [Fact]
    public void Validate_accepts_valid_sql()
    {
        Assert.Null(_p.Validate("select o.id from orders o join \"OrderItems\" i on i.order_id = o.id"));
    }

    [Fact]
    public void Validate_reports_the_error_position()
    {
        const string sql = "select * form orders";
        var e = _p.Validate(sql);
        Assert.NotNull(e);
        Assert.Contains("form", e!.Message);
        Assert.Equal(sql.IndexOf("form", StringComparison.Ordinal), e.Offset);
    }

    [Fact]
    public void Validate_error_offset_is_utf16_after_non_ascii_text()
    {
        const string sql = "select 'éé', * form orders"; // `form` would be a valid alias after a literal, but not after *
        var e = _p.Validate(sql);
        Assert.NotNull(e);
        Assert.Equal(sql.IndexOf("form", StringComparison.Ordinal), e!.Offset);
    }

    [Fact]
    public void Validate_flags_incomplete_statements()
    {
        Assert.NotNull(_p.Validate("select * from "));
        Assert.NotNull(_p.Validate("select o.id from orders o where o."));
    }

    [Fact]
    public void Validate_does_not_catch_semantic_problems()
    {
        // "bob" is a perfectly good identifier to the grammar; catching it is the identifier resolver's job.
        Assert.Null(_p.Validate("select * from orders where name = \"bob\""));
    }
}
