using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

public class StatementPickerTests
{
    /// <summary>Marks the cursor with '|' (and a selection with '[' ']'), returns the clean text and offsets.</summary>
    private static (string Text, int Start, int End) Parse(string marked)
    {
        var sel = marked.IndexOf('[');
        if (sel >= 0)
        {
            var s = marked.Remove(sel, 1);
            var e = s.IndexOf(']');
            return (s.Remove(e, 1), sel, e);
        }
        var c = marked.IndexOf('|');
        return (marked.Remove(c, 1), c, c);
    }

    /// <summary>Statement ranges for simple test scripts (no ';' inside strings): split on ';', trim, drop empties.
    /// Comment-only chunks are dropped too, as the real parser does.</summary>
    private static List<SqlRange> Ranges(string text)
    {
        var list = new List<SqlRange>();
        var pos = 0;
        foreach (var chunk in text.Split(';'))
        {
            // Skip leading whitespace and whole-line comments, like the real parser's trimmed ranges.
            var lead = 0;
            while (lead < chunk.Length)
            {
                if (char.IsWhiteSpace(chunk[lead])) { lead++; continue; }
                if (string.CompareOrdinal(chunk, lead, "--", 0, 2) == 0)
                {
                    var nl = chunk.IndexOf('\n', lead);
                    lead = nl < 0 ? chunk.Length : nl + 1;
                    continue;
                }
                break;
            }
            var body = chunk[lead..].TrimEnd();
            if (body.Length > 0) list.Add(new SqlRange(pos + lead, body.Length));
            pos += chunk.Length + 1;
        }
        return list;
    }

    private static (string? Sql, PickReason Reason) Run(string marked)
    {
        var (text, s, e) = Parse(marked);
        var pick = StatementPicker.Pick(text, Ranges(text), s, e);
        return (pick.Range is { } r ? text.Substring(r.Start, r.Length) : null, pick.Reason);
    }

    [Fact]
    public void Selected_text_wins_and_is_trimmed()
    {
        var (sql, reason) = Run("select 1;\n[  select 2 from t  ];\nselect 3;");
        Assert.Equal("select 2 from t", sql);
        Assert.Equal(PickReason.Selection, reason);
    }

    [Fact]
    public void A_selection_can_span_statements()
    {
        var (sql, reason) = Run("[select 1; select 2];");
        Assert.Equal("select 1; select 2", sql);
        Assert.Equal(PickReason.Selection, reason);
    }

    [Fact]
    public void A_whitespace_only_selection_falls_back_to_the_cursor_rule()
    {
        var (sql, reason) = Run("select 1;\nselect 2;\n[   ]\nselect 3;");
        Assert.NotEqual(PickReason.Selection, reason);
        Assert.Equal("select 2", sql); // blank space below statement 2
    }

    [Theory]
    [InlineData("|select 1;\nselect 2;", "select 1")]       // start of a statement
    [InlineData("sel|ect 1;\nselect 2;", "select 1")]       // middle
    [InlineData("select 1|;\nselect 2;", "select 1")]       // touching its end
    [InlineData("select 1;\nselect| 2;", "select 2")]
    [InlineData("select 1;\nselect 2;|", "select 2")]       // right after the terminator
    public void The_cursor_picks_the_statement_it_is_in(string marked, string expected)
    {
        var (sql, reason) = Run(marked);
        Assert.Equal(expected, sql);
        Assert.True(reason is PickReason.CursorStatement or PickReason.StatementAbove);
    }

    [Fact]
    public void Right_after_the_terminator_means_the_statement_above()
    {
        var (sql, reason) = Run("select 1;|\nselect 2;");
        Assert.Equal("select 1", sql);
        Assert.Equal(PickReason.StatementAbove, reason);
    }

    [Fact]
    public void A_blank_line_runs_the_statement_above_it()
    {
        var (sql, reason) = Run("select 1;\n|\nselect 2;");
        Assert.Equal("select 1", sql);
        Assert.Equal(PickReason.StatementAbove, reason);
    }

    [Fact]
    public void Several_blank_lines_below_a_statement_still_pick_it()
    {
        Assert.Equal("select 1", Run("select 1;\n\n\n|\n\nselect 2;").Sql);
    }

    [Fact]
    public void Blank_space_after_the_last_statement_picks_it()
    {
        Assert.Equal("select 2", Run("select 1;\nselect 2\n\n|").Sql);
    }

    [Fact]
    public void Nothing_sensible_nearby_runs_nothing()
    {
        // Cursor in a comment above the first of two statements.
        var (sql, reason) = Run("-- |notes\nselect 1;\nselect 2;");
        Assert.Null(sql);
        Assert.Equal(PickReason.None, reason);
    }

    [Fact]
    public void A_comment_line_between_statements_is_not_blank_space()
    {
        var (sql, reason) = Run("select 1;\n-- n|ote\nselect 2;");
        Assert.Null(sql);
        Assert.Equal(PickReason.None, reason);
    }

    [Fact]
    public void A_sheet_with_one_statement_runs_it_from_anywhere()
    {
        var (sql, reason) = Run("-- |scratch\n\n\nselect 1");
        Assert.Equal("select 1", sql);
        Assert.Equal(PickReason.OnlyStatement, reason);
    }

    [Fact]
    public void An_empty_sheet_runs_nothing()
    {
        var (sql, reason) = Run("|");
        Assert.Null(sql);
        Assert.Equal(PickReason.None, reason);

        Assert.Equal(PickReason.None, Run("-- just a| comment").Reason);
    }

    [Fact]
    public void A_second_semicolon_is_not_blank_space()
    {
        // ";;" leaves the cursor past two terminators: not "right below" the statement.
        Assert.Null(Run("select 1;;|\nselect 2;").Sql);
    }

    [Fact]
    public void Out_of_range_offsets_are_clamped()
    {
        var text = "select 1";
        var pick = StatementPicker.Pick(text, Ranges(text), 500, 900);
        Assert.Equal(PickReason.CursorStatement, pick.Reason);
        var neg = StatementPicker.Pick(text, Ranges(text), -5, -5);
        Assert.Equal(PickReason.CursorStatement, neg.Reason);
    }
}
