namespace SiftDB.Abstractions;

/// <summary>How a dialect writes identifiers (design section 3).</summary>
public interface IIdentifierPolicy
{
    /// <summary>The identifier as it must be written to mean exactly this name: bare when that is safe, quoted
    /// when it is not (mixed case, special characters, words the grammar reserves).</summary>
    string Quote(string identifier);
}
