using System.Text.RegularExpressions;
using SiftDB.Abstractions;

namespace SiftDB.Engine;

/// <summary>
/// One result styling rule (design section 10): where it applies, a condition, and a style. Built-in presets are rules
/// like any other; only their definition comes from <see cref="StyleRules.Presets"/> rather than the settings file.
/// </summary>
/// <param name="Id">Stable. Presets are "preset:…"; the user's rules get a random id.</param>
/// <param name="Columns">A wildcard pattern over the column name (<c>*</c>, <c>?</c>, case-insensitive). Empty: every column.</param>
/// <param name="Types">Only columns of these generic types. Empty: any type.</param>
/// <param name="Condition">null, equals, oneOf, range, negative or pattern.</param>
/// <param name="Values">The condition's operands: equals one, oneOf a list, range [min, max] ("" for open), pattern one.</param>
/// <param name="Text">Text colour, #rgb or #rrggbb. Every rule sets both colours, so a cell reads the same in either theme.</param>
public sealed record StyleRule(
    string Id,
    string Name,
    bool Enabled,
    string Columns,
    IReadOnlyList<GenericType> Types,
    string Condition,
    IReadOnlyList<string> Values,
    string Text,
    string Background,
    bool Bold)
{
    public bool IsPreset => Id.StartsWith(StyleRules.PresetPrefix, StringComparison.Ordinal);
}

public static partial class StyleRules
{
    public const string PresetPrefix = "preset:";

    public static readonly IReadOnlyList<string> Conditions = ["null", "equals", "oneOf", "range", "negative", "pattern", "past", "future"];

    /// <summary>
    /// The built-in rules, in the order they first appear (at the bottom of the list, so the user's rules win). Colours
    /// are the app's flat tones: grey, green, amber and red, each a text colour on its own tint.
    /// </summary>
    public static readonly IReadOnlyList<StyleRule> Presets =
    [
        new("preset:null", "NULL values", true, "", [], "null", [], "#59636e", "#e6e9ed", false),
        new("preset:true", "True", true, "", [GenericType.Boolean], "oneOf", ["true", "t", "yes", "y", "1"], "#1d6b35", "#d5ebdb", false),
        new("preset:false", "False", true, "", [GenericType.Boolean], "oneOf", ["false", "f", "no", "n", "0"], "#7a5a00", "#f0e6c2", false),
        new("preset:negative", "Negative numbers", true, "", [GenericType.Integer, GenericType.Decimal], "negative", [], "#b42630", "#f3dbdc", false),
        // Off until switched on: most tables have a created_at, and colouring every date in it would be loud.
        new("preset:past", "Dates in the past", false, "", [GenericType.Date, GenericType.DateTime], "past", [], "#7a5a00", "#f0e6c2", false),
        new("preset:future", "Dates in the future", false, "", [GenericType.Date, GenericType.DateTime], "future", [], "#1d6b35", "#d5ebdb", false),
    ];

    public static IReadOnlyList<StyleRule> Default => Presets;

    /// <summary>
    /// The list as it is kept: a preset takes its definition from <see cref="Presets"/> (only whether it is on, and its
    /// place, come from <paramref name="rules"/>); unknown presets and repeated ids are dropped; presets missing from the
    /// list are added at the bottom. A user rule that is not valid throws when <paramref name="strict"/>, else it is
    /// dropped (a hand-edited or newer file must not stop the app).
    /// </summary>
    public static IReadOnlyList<StyleRule> Normalise(IEnumerable<StyleRule> rules, bool strict)
    {
        var result = new List<StyleRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule.IsPreset)
            {
                var preset = Presets.FirstOrDefault(p => p.Id == rule.Id);
                if (preset is not null && seen.Add(preset.Id)) result.Add(preset with { Enabled = rule.Enabled });
                continue;
            }
            var checkedRule = Check(rule, out var problem);
            if (checkedRule is null)
            {
                if (strict) throw new ArgumentException(problem);
                continue;
            }
            if (!seen.Add(checkedRule.Id)) checkedRule = checkedRule with { Id = NewId() };
            seen.Add(checkedRule.Id);
            result.Add(checkedRule);
        }
        foreach (var p in Presets) if (!seen.Contains(p.Id)) result.Add(p);
        return result;
    }

    /// <summary>A tidied copy of a user rule, or null with the reason it cannot be kept.</summary>
    private static StyleRule? Check(StyleRule r, out string problem)
    {
        var name = r.Name.Trim();
        var label = name.Length > 0 ? $"The rule '{name}'" : "A rule";
        problem = "";
        if (!Conditions.Contains(r.Condition)) { problem = $"{label} has an unknown condition '{r.Condition}'."; return null; }
        if (!IsColour(r.Text) || !IsColour(r.Background))
        {
            problem = $"{label} needs both a text and a background colour, as #rgb or #rrggbb.";
            return null;
        }
        var values = r.Values.Select(v => v.Trim()).ToList();
        switch (r.Condition)
        {
            case "equals" or "pattern" when values.Count != 1 || values[0].Length == 0:
                problem = $"{label} needs a value to compare with.";
                return null;
            case "oneOf":
                values = values.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (values.Count == 0) { problem = $"{label} needs at least one value in its list."; return null; }
                break;
            case "range":
                if (values.Count != 2 || values.All(v => v.Length == 0) || values.Any(v => v.Length > 0 && !IsNumber(v) && !IsDate(v)))
                {
                    problem = $"{label} needs a lowest or highest number or date (or both). Dates are written 2026-01-31.";
                    return null;
                }
                break;
            case "null" or "negative" or "past" or "future":
                values = [];
                break;
        }
        var id = string.IsNullOrEmpty(r.Id) ? NewId() : r.Id;
        return r with
        {
            Id = id, Name = name, Columns = r.Columns.Trim(), Types = r.Types.Distinct().ToList(), Values = values,
            Text = r.Text.ToLowerInvariant(), Background = r.Background.ToLowerInvariant(),
        };
    }

    /// <summary>Generic types by their wire names ("integer", "dateTime"); names this version does not know are left out.</summary>
    public static IReadOnlyList<GenericType> ParseTypes(IEnumerable<string>? names) =>
        (names ?? []).Select(n => Enum.TryParse<GenericType>(n, ignoreCase: true, out var t) ? t : (GenericType?)null)
            .OfType<GenericType>().Distinct().ToList();

    public static bool IsColour(string s) => ColourPattern().IsMatch(s);

    /// <summary>An ISO date, optionally with a time: 2026-01-31, 2026-01-31 14:05, 2026-01-31T14:05:09.</summary>
    private static bool IsDate(string s) =>
        DateTime.TryParseExact(s, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    private static bool IsNumber(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex ColourPattern();
}
