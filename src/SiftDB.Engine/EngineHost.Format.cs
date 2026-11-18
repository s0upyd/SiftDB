using System.Text;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// Format SQL (design section 14, F7): the statements a selection touches, the one under the cursor, or the whole sheet,
/// laid out by <see cref="SqlFormatter"/> and applied only if each still means exactly the same. The answer is one edit
/// (the span from the first statement formatted to the last, text between statements kept as it was), so one Undo
/// takes it back.
/// </summary>
public sealed partial class EngineHost
{
    private void FormatSql(int id, int connectionId, string text, int selectionStart, int selectionEnd, bool whole)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) { Error(id, "Unknown connection"); return; }
        var provider = state.Provider;
        var parser = StatementSplitter.For(provider.Parser);
        var statements = parser.Split(text);

        List<SqlRange> chosen;
        if (whole) chosen = [.. statements];
        else if (selectionEnd > selectionStart) chosen = statements.Where(s => s.Start < selectionEnd && s.End > selectionStart).ToList();
        else chosen = StatementPicker.Pick(text, statements, selectionStart, selectionEnd).Range is { } r
            ? statements.Where(s => s.Start == r.Start).ToList() : [];
        if (chosen.Count == 0) { Answer(id, "Nothing to format: put the cursor in a statement, or select some.", "info"); return; }
        if (provider.SameMeaning("select 1", "select 1") is null) { Answer(id, "Formatting is not available for this database.", "warning"); return; }

        var options = CurrentSettings.Format;
        var line = (int offset) => text.AsSpan(0, offset).Count('\n') + 1;
        var problems = new List<string>();
        var formatted = new List<(SqlRange Range, string Text)>();
        foreach (var r in chosen)
        {
            var sql = text.Substring(r.Start, r.Length);
            if (provider.Parser.Validate(sql) is { } error)
            {
                problems.Add($"line {line(r.Start + error.Offset)} does not parse ({error.Message}): fix it first");
                continue;
            }
            // A statement that starts indented keeps that indent on every line.
            var lineStart = text.LastIndexOf('\n', Math.Max(0, r.Start - 1)) + 1;
            var baseIndent = text.AsSpan(lineStart, r.Start - lineStart).IsWhiteSpace() ? r.Start - lineStart : 0;
            var tokens = provider.Parser.Tokenize(sql);
            var result = SqlFormatter.Format(sql, tokens, options, baseIndent);
            // The promise: the same tokens (comments too), the same parse tree, and still one statement.
            var safe = SqlFormatter.SameTokens(sql, tokens, result, provider.Parser.Tokenize(result))
                && provider.SameMeaning(sql, result) == true
                && parser.Split(result).Count == 1;
            if (!safe) { problems.Add($"the statement on line {line(r.Start)} could not be formatted safely, so it was left as it is"); continue; }
            formatted.Add((r, result));
        }

        if (formatted.Count == 0) { Answer(id, $"Nothing was formatted: {problems[0]}.", "warning"); return; }
        var start = formatted[0].Range.Start;
        var end = formatted[^1].Range.End;
        var sb = new StringBuilder();
        var at = start;
        foreach (var (range, result) in formatted)
        {
            sb.Append(text, at, range.Start - at); // what lies between statements stays as it was
            sb.Append(result);
            at = range.End;
        }
        var replacement = sb.ToString();
        var note = problems.Count == 0 ? null
            : $"Formatted {formatted.Count} of {chosen.Count} statements; {problems[0]}{(problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "")}.";
        if (replacement == text.Substring(start, end - start)) { Answer(id, note ?? "Already formatted.", note is null ? "info" : "warning"); return; }
        Event(new { type = "formatResult", replyTo = id, start, length = end - start, text = replacement, message = note, kind = "warning" });
    }

    private void Answer(int id, string message, string kind) =>
        Event(new { type = "formatResult", replyTo = id, message, kind });
}
