using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2.Model;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// An attribute as the grid shows it (design section 14, F19): a string or a number as itself, a boolean as true or
/// false, NULL as SQL NULL, binary as base64, and maps, lists and sets as JSON (sets as arrays). The SDK leaves every
/// member it didn't receive null, so exactly one is set.
/// </summary>
internal static partial class AttributeText
{
    /// <summary>DynamoDB's type letters: S, N, B, BOOL, NULL, M, L, SS, NS, BS.</summary>
    public static string TypeOf(AttributeValue v) =>
        v.S is not null ? "S" : v.N is not null ? "N" : v.BOOL is not null ? "BOOL" : v.NULL == true ? "NULL"
        : v.M is not null ? "M" : v.L is not null ? "L" : v.SS is not null ? "SS" : v.NS is not null ? "NS"
        : v.BS is not null ? "BS" : v.B is not null ? "B" : "NULL";

    /// <summary>The cell's text; null for a NULL.</summary>
    public static string? Display(AttributeValue v) => TypeOf(v) switch
    {
        "S" => v.S,
        "N" => v.N,
        "BOOL" => v.BOOL == true ? "true" : "false",
        "NULL" => null,
        "B" => Convert.ToBase64String(v.B.ToArray()),
        _ => Json(v),
    };

    /// <summary>A whole value as JSON: maps as objects, lists and sets as arrays, numbers as numbers.</summary>
    public static string Json(AttributeValue v)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(w, v);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Several attributes as one JSON object (the "…other" column).</summary>
    public static string Json(IEnumerable<KeyValuePair<string, AttributeValue>> attributes)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            foreach (var (name, value) in attributes) { w.WritePropertyName(name); Write(w, value); }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter w, AttributeValue v)
    {
        switch (TypeOf(v))
        {
            case "S": w.WriteStringValue(v.S); break;
            case "N": Number(w, v.N); break;
            case "BOOL": w.WriteBooleanValue(v.BOOL == true); break;
            case "NULL": w.WriteNullValue(); break;
            case "B": w.WriteStringValue(Convert.ToBase64String(v.B.ToArray())); break;
            case "M":
                w.WriteStartObject();
                foreach (var (name, value) in v.M) { w.WritePropertyName(name); Write(w, value); }
                w.WriteEndObject();
                break;
            case "L":
                w.WriteStartArray();
                foreach (var item in v.L) Write(w, item);
                w.WriteEndArray();
                break;
            case "SS":
                w.WriteStartArray();
                foreach (var s in v.SS) w.WriteStringValue(s);
                w.WriteEndArray();
                break;
            case "NS":
                w.WriteStartArray();
                foreach (var n in v.NS) Number(w, n);
                w.WriteEndArray();
                break;
            case "BS":
                w.WriteStartArray();
                foreach (var b in v.BS) w.WriteStringValue(Convert.ToBase64String(b.ToArray()));
                w.WriteEndArray();
                break;
        }
    }

    /// <summary>A DynamoDB number as a JSON number; one JSON can't write (it never should happen) as a string.</summary>
    private static void Number(Utf8JsonWriter w, string n)
    {
        if (JsonNumber().IsMatch(n)) w.WriteRawValue(n);
        else w.WriteStringValue(n);
    }

    [GeneratedRegex(@"^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$")]
    private static partial Regex JsonNumber();
}
