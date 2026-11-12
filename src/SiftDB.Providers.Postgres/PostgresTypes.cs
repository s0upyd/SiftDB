using SiftDB.Abstractions;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Postgres types onto the generic ones (design section 10). Npgsql reports a column's type by its display name
/// ("integer", "timestamp with time zone", "bit(1)", "numeric(10,2)", "integer[]"); the internal names ("int4",
/// "timestamptz") are accepted too. An array, a range or anything unknown is Text.
/// </summary>
public static class PostgresTypes
{
    private static readonly Dictionary<string, GenericType> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smallint"] = GenericType.Integer, ["integer"] = GenericType.Integer, ["bigint"] = GenericType.Integer,
        ["int2"] = GenericType.Integer, ["int4"] = GenericType.Integer, ["int8"] = GenericType.Integer, ["int"] = GenericType.Integer,
        ["numeric"] = GenericType.Decimal, ["decimal"] = GenericType.Decimal, ["real"] = GenericType.Decimal,
        ["double precision"] = GenericType.Decimal, ["float4"] = GenericType.Decimal, ["float8"] = GenericType.Decimal,
        ["money"] = GenericType.Decimal,
        ["boolean"] = GenericType.Boolean, ["bool"] = GenericType.Boolean,
        ["date"] = GenericType.Date,
        ["timestamp without time zone"] = GenericType.DateTime, ["timestamp with time zone"] = GenericType.DateTime,
        ["timestamp"] = GenericType.DateTime, ["timestamptz"] = GenericType.DateTime,
    };

    public static GenericType GenericTypeOf(string dataTypeName)
    {
        var name = dataTypeName.Trim();
        if (name.EndsWith("[]", StringComparison.Ordinal)) return GenericType.Text; // an array shows as a list
        // A schema-qualified built-in ("pg_catalog.int4") is the type itself.
        if (name.StartsWith("pg_catalog.", StringComparison.OrdinalIgnoreCase)) name = name["pg_catalog.".Length..];

        // Modifiers: numeric(10,2), timestamp(3) with time zone, bit(1), character varying(20).
        var open = name.IndexOf('(');
        string? modifier = null;
        if (open >= 0 && name.IndexOf(')', open) is var close and > 0)
        {
            modifier = name[(open + 1)..close].Trim();
            name = (name[..open] + name[(close + 1)..]).Trim();
        }

        // bit(1) is how SQL Server-style flags are kept in Postgres, and Npgsql reads it as a bool. Wider bit strings are text.
        if (name.Equals("bit", StringComparison.OrdinalIgnoreCase)) return modifier is null or "1" ? GenericType.Boolean : GenericType.Text;
        return Names.TryGetValue(name, out var t) ? t : GenericType.Text;
    }
}
