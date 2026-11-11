using System.Diagnostics;
using System.Text;
using PgSqlParser;
using Xunit.Abstractions;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>
/// Characterization of the libpg_query wrapper (pgsqlparser). It prints what the library really does on the cases
/// SiftDB depends on, so the ISqlParser contract is designed from observed behaviour. Read the output with:
///   dotnet test --filter PgSqlParserCharacterization --logger "console;verbosity=detailed"
/// </summary>
public class PgSqlParserCharacterization(ITestOutputHelper o)
{
    private void Split(string label, string sql)
    {
        o.WriteLine($"== split: {label}");
        foreach (var (name, r) in new[] { ("scanner", Parser.SplitWithScanner(sql)), ("parser ", Parser.SplitWithParser(sql)) })
        {
            if (r.Error is not null) { o.WriteLine($"  {name}: ERROR {r.Error.Message}"); continue; }
            o.WriteLine($"  {name}: " + string.Join(" | ",
                r.Value!.Statements.Select(s => $"[{s.Location}+{s.Length}] {s.Text.Trim().Replace("\n", "\\n")}")));
        }
    }

    [Fact]
    public void Splitting()
    {
        Split("quotes, comments, dollar bodies", "select 1; select ';' ; /* ; */ select $$a;b$$; select 'é';select 2");
        Split("function with body", "create function f() returns int as $$ begin return 1; end $$ language plpgsql; select f();");
        Split("incomplete tail (mid-typing)", "select 1; select * from orders o where o.");
        Split("incomplete middle", "select * from ; select 2; select 3");
        Split("unterminated string", "select 1; select 'abc; select 3");
        Split("blank and comment only", "select 1;\n-- nothing\n;\n\nselect 2");

        var good = Parser.SplitWithScanner("select 1; select 2");
        Assert.Null(good.Error);
        Assert.Equal(2, good.Value!.Statements.Count);
    }

    [Fact]
    public void Scanning()
    {
        var q = "select \"OrderItems\", OrderItems.id from OrderItems where name = \"bob\" and x = 'it''s'  -- c";
        var scan = Parser.Scan(q);
        Assert.Null(scan.Error);
        o.WriteLine("== scan tokens");
        foreach (var t in scan.Value!.Tokens)
            o.WriteLine($"  [{t.Start,3}-{t.End,3}] {t.Token,-10} {t.KeywordKind,-18} '{q[t.Start..t.End]}'");

        o.WriteLine("== scan on incomplete / broken input");
        foreach (var s in new[] { "select * from orders o where o.", "select 'abc", "select \"abc", "select /* open" })
        {
            var r = Parser.Scan(s);
            o.WriteLine($"  '{s}' -> " + (r.Error is not null
                ? "ERROR " + r.Error.Message
                : $"{r.Value!.Tokens.Count} tokens: " + string.Join(" ", r.Value.Tokens.Select(t => t.Token))));
        }

        o.WriteLine("== offsets with non-ASCII text");
        var u = "select 'ééé', x from t";
        var last = Parser.Scan(u).Value!.Tokens[^1];
        o.WriteLine($"  last token start={last.Start}; UTF-16 index of 't'={u.LastIndexOf('t')}; UTF-8 byte index={Encoding.UTF8.GetByteCount(u[..u.LastIndexOf('t')])}");
    }

    [Fact]
    public void ParseErrors()
    {
        o.WriteLine("== parse");
        foreach (var s in new[] { "select * from ", "select * form orders", "select * from orders where name = \"bob\"", "select o.id from orders o where o." })
        {
            var r = Parser.Parse(s, new ParserOptions());
            o.WriteLine($"  '{s}' -> " + (r.Error is not null ? $"ERROR '{r.Error.Message}' cursorPos={r.Error.CursorPos} (utf16 offset {r.Error.GetCursorCharOffset(s)})" : "ok"));
        }
    }

    [Fact]
    public void Performance()
    {
        var big = string.Join("\n", Enumerable.Range(0, 500).Select(i =>
            $"select a.id, b.\"OrderItems\", c.x{i} from a join b on a.id = b.id left join c on c.id = a.id where a.v > {i} and b.s = 'text{i}';"));
        o.WriteLine($"== perf: {big.Length / 1024} KB, 500 statements");
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew(); var r1 = Parser.Scan(big); var a = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); Parser.SplitWithScanner(big); var b = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); Parser.SplitWithParser(big); var c = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); Parser.Parse(big, new ParserOptions()); var d = sw.Elapsed.TotalMilliseconds;
            o.WriteLine($"  run {i + 1}: scan {a:F1} ms ({r1.Value!.Tokens.Count} tokens) · splitScanner {b:F1} ms · splitParser {c:F1} ms · parse {d:F1} ms");
        }
        var one = "select o.id, i.\"Sku\" from orders o join \"OrderItems\" i on i.order_id = o.id where o.total > 10";
        var swt = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) Parser.Parse(one, new ParserOptions());
        o.WriteLine($"  single statement parse: {swt.Elapsed.TotalMilliseconds / 1000:F3} ms avg over 1000");
    }
}
