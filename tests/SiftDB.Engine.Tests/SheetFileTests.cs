using System.Text;
using SiftDB.Engine;

namespace SiftDB.Engine.Tests;

/// <summary>Sheet files (design section 6): the header is SiftDB's, everything else is kept as the file had it.</summary>
public class SheetFileTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void The_header_is_taken_off_and_read()
    {
        var c = SheetFile.Parse(Utf8("-- db: connection=dev-pg; mode=strict\r\nselect 1;\r\n"));
        Assert.Equal("select 1;\n", c.Text);
        Assert.Equal("dev-pg", c.Header.Connection);
        Assert.Equal("strict", c.Header.Mode);
        Assert.Equal(SheetFile.Crlf, c.LineEnding);
        Assert.Equal("utf-8", c.Encoding);
    }

    [Fact]
    public void A_file_without_a_header_is_left_whole()
    {
        var c = SheetFile.Parse(Utf8("-- my own comment\nselect 1;"));
        Assert.Equal("-- my own comment\nselect 1;", c.Text);
        Assert.Null(c.Header.Connection);
        Assert.Null(c.Header.Mode);
        Assert.Equal(SheetFile.Lf, c.LineEnding);
    }

    [Fact]
    public void Only_the_first_line_can_be_the_header()
    {
        var c = SheetFile.Parse(Utf8("select 1;\n-- db: connection=x\n"));
        Assert.Null(c.Header.Connection);
        Assert.Contains("-- db: connection=x", c.Text);
    }

    [Theory]
    [InlineData("--db:connection=a;mode=assisted", "a", "assisted")]
    [InlineData("  --  DB:  Connection = a ;  MODE = Strict  ", "a", "strict")]
    [InlineData("-- db: mode=sideways", null, null)] // a mode it does not know is no mode
    [InlineData("-- db: connection=\"shop; eu\"", "shop; eu", null)]
    [InlineData("-- db: connection=\"say \"\"hi\"\"\"", "say \"hi\"", null)]
    [InlineData("-- db: junk; connection=a", "a", null)]
    public void Header_parsing_is_forgiving(string line, string? connection, string? mode)
    {
        var c = SheetFile.Parse(Utf8(line + "\nselect 1"));
        Assert.Equal(connection, c.Header.Connection);
        Assert.Equal(mode, c.Header.Mode);
        Assert.Equal("select 1", c.Text);
    }

    [Fact]
    public void Keys_it_does_not_know_are_kept_in_order()
    {
        var c = SheetFile.Parse(Utf8("-- db: vars=Smith; connection=a; colour=red\nx"));
        Assert.Equal(["vars", "colour"], c.Header.Other.Select(p => p.Key));
        var line = SheetFile.FormatHeader(c.Header);
        Assert.Equal("-- db: connection=a; vars=Smith; colour=red", line);
    }

    [Fact]
    public void Values_that_need_it_are_quoted_and_read_back_the_same()
    {
        var header = new SheetHeader("shop; \"eu\" = west ", "assisted", []);
        var bytes = SheetFile.Compose("select 1", header, SheetFile.Lf, "utf-8");
        Assert.Equal(header.Connection, SheetFile.Parse(bytes).Header.Connection);
    }

    [Fact]
    public void Writing_puts_the_header_first_and_keeps_line_endings()
    {
        var bytes = SheetFile.Compose("select 1;\nselect 2;\n", new SheetHeader("dev", "assisted", []), SheetFile.Crlf, "utf-8");
        Assert.Equal("-- db: connection=dev; mode=assisted\r\nselect 1;\r\nselect 2;\r\n", Encoding.UTF8.GetString(bytes));
        var lf = SheetFile.Compose("a\nb", SheetHeader.Empty, SheetFile.Lf, "utf-8");
        Assert.Equal("a\nb", Encoding.UTF8.GetString(lf)); // nothing to say: no header line at all
    }

    [Fact]
    public void Mixed_line_endings_follow_the_majority()
    {
        Assert.Equal(SheetFile.Crlf, SheetFile.Parse(Utf8("a\r\nb\r\nc\nd")).LineEnding);
        Assert.Equal(SheetFile.Lf, SheetFile.Parse(Utf8("a\nb\nc\r\nd")).LineEnding);
        Assert.Equal(SheetFile.Crlf, SheetFile.Parse(Utf8("one line")).LineEnding); // Windows by default
        Assert.Equal("a\nb\nc", SheetFile.Parse(Utf8("a\r\nb\rc")).Text);
    }

    [Fact]
    public void A_byte_order_mark_is_kept()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF, .. Utf8("select 'é';")];
        var c = SheetFile.Parse(bom);
        Assert.Equal("utf-8-bom", c.Encoding);
        Assert.Equal("select 'é';", c.Text);
        var written = SheetFile.Compose(c.Text, c.Header, c.LineEnding, c.Encoding);
        Assert.Equal(bom, written);
    }

    [Fact]
    public void Utf16_files_round_trip()
    {
        var bytes = new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("select 'ü';\r\n")).ToArray();
        var c = SheetFile.Parse(bytes);
        Assert.Equal("utf-16le", c.Encoding);
        Assert.Equal("select 'ü';\n", c.Text);
        Assert.Equal(bytes, SheetFile.Compose(c.Text, c.Header, c.LineEnding, c.Encoding));
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_refused_rather_than_damaged()
    {
        var latin1 = new byte[] { (byte)'s', (byte)'e', (byte)'l', 0xE9, (byte)';' }; // 'é' in Windows-1252
        var ex = Assert.Throws<InvalidDataException>(() => SheetFile.Parse(latin1));
        Assert.Contains("not UTF-8", ex.Message);
    }

    [Fact]
    public void Write_and_read_a_real_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"siftdb-{Guid.NewGuid():N}.sql");
        try
        {
            SheetFile.Write(path, "select 1;\n", new SheetHeader("dev", "strict", []), SheetFile.Crlf, "utf-8");
            var c = SheetFile.Read(path);
            Assert.Equal(("select 1;\n", "dev", "strict"), (c.Text, c.Header.Connection, c.Header.Mode));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
