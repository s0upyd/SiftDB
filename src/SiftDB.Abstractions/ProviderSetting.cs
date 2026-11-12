namespace SiftDB.Abstractions;

/// <summary>One option of a <see cref="ProviderSetting"/>: the stored value, how it reads, and what it means.</summary>
public sealed record SettingChoice(string Value, string Label, string? Help = null);

/// <summary>
/// A user setting that only makes sense for one kind of database (design section 10), described by its provider so the
/// settings editor stays database-neutral: it draws a category per provider from these. The engine stores the chosen
/// values and hands them to <see cref="IDatabaseProvider.ApplySettings"/>; it never interprets them.
/// </summary>
/// <param name="Key">Stable: the stored value is filed under it.</param>
/// <param name="Default">One of the <paramref name="Choices"/>' values, used until the user picks another.</param>
public sealed record ProviderSetting(string Key, string Label, string Default, IReadOnlyList<SettingChoice> Choices, string? Help = null);
