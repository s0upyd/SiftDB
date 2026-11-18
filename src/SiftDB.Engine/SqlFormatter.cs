using System.Text;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

public enum KeywordCase { AsTyped, Upper, Lower }

public enum CommaPlacement { End, Start }

/// <summary>How Format SQL lays statements out (design section 14, F7). The settings' SQL category holds the user's choice.</summary>
public sealed record FormatOptions(
    KeywordCase KeywordCase = KeywordCase.AsTyped, int Indent = 4, CommaPlacement Commas = CommaPlacement.End, int LineWidth = 100)
{
    public static readonly FormatOptions Default = new();
}

/// <summary>
/// Format SQL (design section 14, F7): lays one statement out from the dialect's own tokens, comments included. It only
/// ever changes whitespace and the case of keywords, never a token's text: strings, quoted identifiers and <c>$$</c>
/// bodies come out exactly as they went in. That is checked afterwards (see <see cref="SameTokens"/>), and the provider
/// compares the two parse trees as well, so a layout bug can refuse to format but can never change what a statement does.
///
/// Statements that read or change data (SELECT, WITH, INSERT, UPDATE, DELETE, VALUES, and EXPLAIN of them) are laid out:
/// a new line for each major clause and join, indented subqueries and CTEs, the conditions of WHERE, HAVING and ON one
/// per line, a long select list (or SET, GROUP BY, ORDER BY, VALUES, RETURNING list) one item per line, and spaces
/// around operators. Anything else (CREATE TABLE, GRANT…) is left exactly as it is: its layout is usually deliberate. A formatted statement never contains a blank line, which would split it in two (§6).
/// </summary>
public static class SqlFormatter
{
    public static string Format(string sql, IReadOnlyList<SqlToken> tokens, FormatOptions options, int baseIndent = 0) =>
        new Layout(sql, tokens, options).Run(baseIndent);

    /// <summary>
    /// True when <paramref name="b"/> has exactly <paramref name="a"/>'s tokens, in order, with the same text (keywords
    /// compared without case): what the formatter promises. Comments count, so one that went missing is caught here; the
    /// parse trees, which have no comments, could not catch that.
    /// </summary>
    public static bool SameTokens(string a, IReadOnlyList<SqlToken> ta, string b, IReadOnlyList<SqlToken> tb)
    {
        if (ta.Count != tb.Count) return false;
        for (var i = 0; i < ta.Count; i++)
        {
            if (ta[i].Kind != tb[i].Kind) return false;
            var x = a.AsSpan(ta[i].Start, ta[i].Length);
            var y = b.AsSpan(tb[i].Start, tb[i].Length);
            if (!(ta[i].Kind == SqlTokenKind.Keyword ? x.Equals(y, StringComparison.OrdinalIgnoreCase) : x.SequenceEqual(y))) return false;
        }
        return true;
    }

    /// <summary>Keywords outside the reserved classes whose case is changed too: they are almost never used as names.</summary>
    private static readonly HashSet<string> AlsoCased =
    [
        "BY", "SET", "INSERT", "UPDATE", "DELETE", "VALUES", "RECURSIVE", "NULLS", "ROWS", "PARTITION", "OVER", "CONFLICT",
        "NOTHING", "BETWEEN", "EXISTS", "EXPLAIN", "ANALYZE", "ANALYSE", "INTERVAL",
    ];

    /// <summary>Statements that are laid out. Anything else keeps its own layout.</summary>
    private static readonly HashSet<string> Laid = ["SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "VALUES", "EXPLAIN", "TABLE"];

    /// <summary>Clauses whose comma-separated items go one per line when the clause is too long for a line.</summary>
    private static readonly HashSet<string> Lists = ["SELECT", "GROUP BY", "ORDER BY", "SET", "VALUES", "RETURNING"];

    /// <summary>Reserved words that stand for a value: an operator after one is binary (<c>null - 1</c>).</summary>
    private static readonly HashSet<string> ValueWords =
    [
        "NULL", "TRUE", "FALSE", "END", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "USER",
        "SESSION_USER", "LOCALTIME", "LOCALTIMESTAMP", "CURRENT_ROLE", "CURRENT_CATALOG",
    ];

    private sealed class Tok
    {
        public required string Text;
        public required string Up;
        public required SqlTokenKind Kind;
        public required KeywordClass Class;
        public bool NewlineBefore, SpaceBefore, SpaceAfter;
        public bool IsComment => Kind == SqlTokenKind.Comment;
        public bool IsLineComment => Kind == SqlTokenKind.Comment && Text.StartsWith("--", StringComparison.Ordinal);
    }

    /// <summary>A level of nesting: the statement, a subquery or CTE body (laid out), or other parentheses (left inline).</summary>
    private sealed class Block
    {
        public bool Inline;
        public int Indent, OuterIndent;
        public bool First = true;
        public string Clause = "";
        public bool ListBreak, ListStartPending, InOn, BetweenPending;
        public int CaseDepth;
    }

    private sealed class Layout(string sql, IReadOnlyList<SqlToken> raw, FormatOptions o)
    {
        private readonly List<Tok> _t = BuildTokens(sql, raw);
        private readonly List<string> _lines = [];
        private readonly StringBuilder _line = new();
        private int _lineIndent;
        private bool _lineHasText;
        private Tok? _last;
        private bool _lastUnary;
        private int? _pendingBreak;
        private readonly Stack<Block> _blocks = new();
        private int Unit => Math.Clamp(o.Indent, 1, 8);

        private static List<Tok> BuildTokens(string sql, IReadOnlyList<SqlToken> raw)
        {
            var list = new List<Tok>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var r = raw[i];
                var text = sql.Substring(r.Start, r.Length);
                var gapStart = i == 0 ? 0 : raw[i - 1].End;
                var gap = sql.AsSpan(gapStart, r.Start - gapStart);
                var after = i + 1 < raw.Count ? sql.AsSpan(r.End, raw[i + 1].Start - r.End) : sql.AsSpan(r.End);
                list.Add(new Tok
                {
                    Text = text, Up = text.ToUpperInvariant(), Kind = r.Kind, Class = r.Keyword,
                    NewlineBefore = i > 0 && gap.Contains('\n'), SpaceBefore = i > 0 && gap.Length > 0, SpaceAfter = after.Length > 0,
                });
            }
            return list;
        }

        public string Run(int baseIndent)
        {
            var first = _t.FirstOrDefault(t => !t.IsComment);
            if (first is null) return sql;
            // Only the statements that read or change data are laid out. The rest (CREATE TABLE, GRANT…) keep their layout,
            // which is usually deliberate, and their case: which of their words are names is not knowable from tokens alone.
            if (!Laid.Contains(first.Up)) return sql;

            _blocks.Push(new Block());
            for (var i = 0; i < _t.Count; i++) i = Step(i);
            Flush();
            var pad = new string(' ', baseIndent);
            return string.Join("\n" + pad, _lines);
        }

        private string Cased(Tok t)
        {
            if (o.KeywordCase == KeywordCase.AsTyped || t.Kind != SqlTokenKind.Keyword) return t.Text;
            if (t.Class is not (KeywordClass.Reserved or KeywordClass.TypeOrFunctionName) && !AlsoCased.Contains(t.Up)) return t.Text;
            return o.KeywordCase == KeywordCase.Upper ? t.Up : t.Text.ToLowerInvariant();
        }

        // ---- the main step ------------------------------------------------------------------------------------------

        /// <summary>Lays out the token at <paramref name="i"/> (and any it consumes with it). Returns the last index used.</summary>
        private int Step(int i)
        {
            var t = _t[i];
            if (t.IsComment) { Comment(i); return i; }
            TakePendingBreak();
            var top = _blocks.Peek();

            if (!top.Inline)
            {
                var n = ClauseWords(i, top, lookahead: false);
                if (n > 0) return Clause(i, n, top);
                if (top.ListStartPending && !(t.Up is "DISTINCT" or "ALL" || (t.Up == "ON" && Prev(i)?.Up == "DISTINCT") || (t.Text == "(" && Prev(i)?.Up == "ON")))
                {
                    top.ListStartPending = false;
                    Break(top.Indent + Unit);
                }
                if (t.Up is "AND" or "OR" && top.CaseDepth == 0 && (top.InOn || top.Clause is "WHERE" or "HAVING"))
                {
                    if (t.Up == "AND" && top.BetweenPending) top.BetweenPending = false;
                    else Break(top.Indent + Unit);
                }
                else if (t.Up == "ON" && top.Clause.EndsWith("JOIN", StringComparison.Ordinal) && !top.InOn)
                {
                    Break(top.Indent + Unit);
                    top.InOn = true;
                }
                else if (t.Up == "BETWEEN") top.BetweenPending = true;
                else if (t.Up == "CASE") top.CaseDepth++;
                else if (t.Up == "END" && top.CaseDepth > 0) top.CaseDepth--;
                else if (t.Text == ",") return Comma(top, t, i);
            }

            if (t.Text == "(") { Open(i); return i; }
            if (t.Text == ")") { Close(t); return i; }
            // Brackets (an array, a subscript) are a group of their own: their commas are not the clause's.
            if (t.Text == "[") { Write(t, i); top.First = false; _blocks.Push(new Block { Inline = true, Indent = top.Indent }); return i; }
            if (t.Text == "]") { if (_blocks.Count > 1 && top.Inline) _blocks.Pop(); Write(t, i); return i; }
            Write(t, i);
            top.First = false;
            return i;
        }

        private int Clause(int i, int n, Block top)
        {
            var words = string.Join(" ", Enumerable.Range(i, n).Select(k => _t[k].Up));
            if (!top.First) Break(top.Indent);
            for (var k = i; k < i + n; k++) Write(_t[k], k);
            top.First = false;
            top.Clause = words;
            top.InOn = false;
            top.BetweenPending = false;
            top.CaseDepth = 0;
            top.ListBreak = Lists.Contains(words) && TooLong(i + n, top, _lineIndent + words.Length + 1);
            top.ListStartPending = top.ListBreak;
            return i + n - 1;
        }

        /// <summary>A comma of the block's own. Returns the last index used (a comment after it may be moved with it).</summary>
        private int Comma(Block top, Tok comma, int i)
        {
            if (top.Clause is "WITH" or "WITH RECURSIVE")
            {
                Write(comma, i);
                _pendingBreak = top.Indent; // the next CTE starts a line of its own
                return i;
            }
            if (!top.ListBreak) { Write(comma, i); return i; }
            // Leading commas, except where a comment follows the comma on its line ("a, -- why"): the comment belongs to a, and
            // moving it in front of the comma would change the order of the tokens, which the formatter never does. That
            // comma stays at the end of a's line.
            var commentAfter = i + 1 < _t.Count && _t[i + 1].IsComment && !_t[i + 1].NewlineBefore;
            if (o.Commas == CommaPlacement.End || commentAfter)
            {
                Write(comma, i);
                _pendingBreak = top.Indent + Unit;
                return i;
            }
            Break(top.Indent + Unit);
            _line.Append(", ");
            _lineHasText = true;
            _last = comma;
            _noSpace = true;
            return i;
        }

        /// <summary>Set when ", " was written by hand (leading commas): the next token follows it with no extra space.</summary>
        private bool _noSpace;

        private void Open(int i)
        {
            var top = _blocks.Peek();
            Write(_t[i], i);
            top.First = false;
            var next = NextSig(i);
            if (next is not null && next.Up is "SELECT" or "WITH" or "VALUES" or "INSERT" or "UPDATE" or "DELETE" or "TABLE")
            {
                var inner = new Block { Indent = _lineIndent + Unit, OuterIndent = _lineIndent };
                _blocks.Push(inner);
                _pendingBreak = inner.Indent;
            }
            else _blocks.Push(new Block { Inline = true, Indent = top.Indent });
        }

        private void Close(Tok close)
        {
            var b = _blocks.Count > 1 ? _blocks.Pop() : _blocks.Peek();
            if (!b.Inline) Break(b.OuterIndent);
            Write(close, -1);
        }

        private void Comment(int i)
        {
            var t = _t[i];
            // A comment that was at the end of a line stays at the end of its line (after "a," even when the next item will go
            // on a line of its own); one that had a line to itself gets one again.
            if (t.NewlineBefore || !_lineHasText)
            {
                TakePendingBreak();
                if (_lineHasText) Break(_lineIndent);
            }
            if (_lineHasText) _line.Append(' ');
            _line.Append(t.Text);
            _lineHasText = true;
            _last = t;
            // A line comment ends its line; a block comment that did keeps doing so.
            if (t.IsLineComment || (i + 1 < _t.Count && _t[i + 1].NewlineBefore)) _pendingBreak ??= _lineIndent;
        }

        // ---- writing ------------------------------------------------------------------------------------------------

        private void TakePendingBreak()
        {
            if (_pendingBreak is not { } indent) return;
            _pendingBreak = null;
            Break(indent);
        }

        /// <summary>Ends the line (if it has anything on it) and starts the next at <paramref name="indent"/>.</summary>
        private void Break(int indent)
        {
            if (_lineHasText) Flush();
            _line.Clear();
            _line.Append(' ', indent);
            _lineIndent = indent;
            _lineHasText = false;
            _last = null;
        }

        private void Flush()
        {
            if (!_lineHasText) return;
            _lines.Add(_line.ToString().TrimEnd());
            _line.Clear();
            _lineHasText = false;
        }

        private void Write(Tok t, int i)
        {
            if (_lineHasText && !_noSpace && SpaceBetween(_last!, t, i)) _line.Append(' ');
            _noSpace = false;
            _line.Append(Cased(t));
            _lineHasText = true;
            _lastUnary = i >= 0 && t.Kind == SqlTokenKind.Operator && t.Text != "*" && IsUnary(i);
            _last = t;
        }

        private bool SpaceBetween(Tok prev, Tok t, int i)
        {
            if (prev.IsComment) return true;
            if (t.Text is "," or ")" or "]" or "." || t.Text == "::") return false;
            if (prev.Text is "(" or "[" or "." || prev.Text == "::") return false;
            if (t.Text is "(" or "[" or ":" || prev.Text == ":") return t.SpaceBefore; // a call, a subscript, a slice: as written
            if (_lastUnary) return t.SpaceBefore; // -1, @name: as written
            return true;
        }

        /// <summary>An operator with nothing that ends a value before it (a sign, a prefix operator): spaced only as written.</summary>
        private bool IsUnary(int i)
        {
            var p = Prev(i);
            if (p is null) return true;
            if (p.Kind == SqlTokenKind.Operator) return true;
            if (p.Kind == SqlTokenKind.Punctuation) return p.Text is not (")" or "]");
            if (p.Kind == SqlTokenKind.Keyword) return p.Class == KeywordClass.Reserved && !ValueWords.Contains(p.Up);
            return false;
        }

        // ---- looking around -----------------------------------------------------------------------------------------

        private Tok? Prev(int i)
        {
            for (var k = i - 1; k >= 0; k--) if (!_t[k].IsComment) return _t[k];
            return null;
        }

        private Tok? NextSig(int i)
        {
            for (var k = i + 1; k < _t.Count; k++) if (!_t[k].IsComment) return _t[k];
            return null;
        }

        private string? Up(int k) => k < _t.Count && !_t[k].IsComment ? _t[k].Up : null;

        /// <summary>
        /// How many tokens from <paramref name="i"/> form a clause keyword of the block (SELECT, GROUP BY, LEFT OUTER JOIN…),
        /// or 0. <paramref name="lookahead"/>: measuring a clause's length, where the block's state is not known yet, so the
        /// words that depend on it (SET, USING, DO, a join's ON) are left out.
        /// </summary>
        private int ClauseWords(int i, Block top, bool lookahead)
        {
            var w = _t[i].Up;
            if (_t[i].Kind != SqlTokenKind.Keyword) return 0;
            var prev = Prev(i)?.Up;
            switch (w)
            {
                case "SELECT" or "WHERE" or "HAVING" or "LIMIT" or "OFFSET" or "FETCH" or "WINDOW" or "RETURNING"
                    or "UNION" or "INTERSECT" or "EXCEPT":
                    return 1;
                case "WITH":
                    return Up(i + 1) == "RECURSIVE" ? 2 : 1;
                case "FROM":
                    return prev == "DISTINCT" ? 0 : 1; // IS DISTINCT FROM is an operator
                case "VALUES":
                    return prev == "DEFAULT" ? 0 : 1;
                case "INSERT":
                    return Up(i + 1) == "INTO" ? 2 : 1;
                case "DELETE":
                    return Up(i + 1) == "FROM" ? 2 : 1;
                case "UPDATE":
                    return prev is "DO" or "FOR" or "KEY" or "NO" ? 0 : 1; // ON CONFLICT DO UPDATE; FOR [NO KEY] UPDATE
                case "GROUP" or "ORDER":
                    return Up(i + 1) == "BY" ? 2 : 0;
                case "FOR":
                    return Up(i + 1) is "UPDATE" or "SHARE" or "NO" or "KEY" ? 1 : 0;
                case "ON":
                    return Up(i + 1) == "CONFLICT" ? 2 : 0;
                case "SET":
                    return !lookahead && top.Clause == "UPDATE" ? 1 : 0; // after ON CONFLICT DO UPDATE it stays on DO's line
                case "DO":
                    return !lookahead && top.Clause == "ON CONFLICT" ? 1 : 0;
                case "USING":
                    return !lookahead && top.Clause == "DELETE FROM" ? 1 : 0;
            }
            // NATURAL? (INNER | CROSS | LEFT | RIGHT | FULL)? OUTER? JOIN, only after FROM or another join.
            if (!lookahead && !(top.Clause == "FROM" || top.Clause.EndsWith("JOIN", StringComparison.Ordinal))) return 0;
            var k = i;
            if (Up(k) == "NATURAL") k++;
            if (Up(k) is "INNER" or "CROSS" or "LEFT" or "RIGHT" or "FULL") k++;
            if (Up(k) == "OUTER") k++;
            return Up(k) == "JOIN" ? k - i + 1 : 0;
        }

        /// <summary>
        /// Whether the clause from <paramref name="from"/> to the next clause of the same block would run past the line width
        /// on one line. A line comment or a subquery in it always breaks it up.
        /// </summary>
        private bool TooLong(int from, Block top, int col)
        {
            var depth = 0;
            var length = col;
            for (var k = from; k < _t.Count; k++)
            {
                var t = _t[k];
                if (t.IsLineComment) return true;
                if (t.Text == "(") { depth++; if (NextSig(k)?.Up is "SELECT" or "WITH") return true; }
                else if (t.Text == ")") { if (--depth < 0) break; }
                else if (depth == 0 && !t.IsComment && ClauseWords(k, top, lookahead: true) > 0) break;
                length += t.Text.Length + 1;
                if (length > o.LineWidth) return true;
            }
            return false;
        }
    }
}
