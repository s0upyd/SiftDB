using System.Collections;
using System.Data.Common;
using Amazon.DynamoDBv2.Model;
using SiftDB.Abstractions;

namespace SiftDB.Providers.DynamoDB;

/// <summary>
/// One statement's items as rows (design section 14, F19). Items don't share a fixed set of columns, so the columns are
/// worked out from the first page: the table's keys first (partition, then sort), then the other attributes in the
/// order first seen; a SELECT that names its attributes keeps its order. A reader's columns can't change once it has
/// started, so attributes first seen on a later page go into one trailing "…other" column, as JSON. The next page is
/// fetched only when the grid has read the last row of this one, so rows stream (design section 1).
/// </summary>
internal sealed class DynamoReader : DbDataReader, IResultNote
{
    public const string OtherColumn = "…other";

    /// <summary>Fetches the page after <c>token</c>: its items and the token for the one after it (null at the end).</summary>
    public delegate Task<(List<Dictionary<string, AttributeValue>> Items, string? Next, double? Units)> PageFetcher(string token, CancellationToken ct);

    private readonly string[] _names;
    private readonly string[] _types;
    private readonly bool _hasOther;
    private readonly Dictionary<string, int> _ordinal;
    private readonly PageFetcher _fetch;
    private List<Dictionary<string, AttributeValue>> _page;
    private string? _next;
    private int _index = -1;
    private readonly string?[] _row;
    /// <summary>The attributes that have a column of their own (the rest go into "…other").</summary>
    private readonly HashSet<string> _known;
    private bool _closed;
    private double? _units;
    private readonly string _unitWord;
    private readonly string? _headline, _detail;

    /// <param name="names">The attribute columns, in order; the "…other" column is added after them when <paramref name="other"/>.</param>
    /// <param name="units">The capacity the first page used (null: not reported, as by DynamoDB Local).</param>
    /// <param name="unitWord">"read" or "write": what the capacity was used for.</param>
    /// <param name="headline">Replaces the status line's headline ("Queued"), or null.</param>
    /// <param name="detail">Said before the capacity ("outside the transaction"), or null.</param>
    public DynamoReader(IReadOnlyList<string> names, bool other, List<Dictionary<string, AttributeValue>> firstPage, string? next, PageFetcher fetch,
        double? units = null, string unitWord = "read", string? headline = null, string? detail = null)
    {
        _units = units;
        _unitWord = unitWord;
        _headline = headline;
        _detail = detail;
        _hasOther = other;
        _names = other ? [.. names, OtherColumn] : [.. names];
        _ordinal = _names.Select((n, i) => (n, i)).GroupBy(x => x.n).ToDictionary(g => g.Key, g => g.First().i);
        _page = firstPage;
        _next = next;
        _fetch = fetch;
        _row = new string?[_names.Length];
        _known = names.ToHashSet(StringComparer.Ordinal);
        // The type a column mostly has on the first page, for GetDataTypeName (and so result styling's number rules).
        _types = _names.Select((n, i) => other && i == _names.Length - 1 ? "M"
            : firstPage.Select(item => item.TryGetValue(n, out var v) ? AttributeText.TypeOf(v) : null).Where(t => t is not null and not "NULL")
                .GroupBy(t => t).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "S").ToArray();
    }

    public override async Task<bool> ReadAsync(CancellationToken ct)
    {
        if (_closed) return false;
        while (++_index >= _page.Count)
        {
            if (_next is not { } token) { _index = _page.Count; return false; }
            double? more;
            (_page, _next, more) = await _fetch(token, ct);
            if (more is { } u) _units = (_units ?? 0) + u;
            _index = -1;
        }
        Fill(_page[_index]);
        return true;
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    private void Fill(Dictionary<string, AttributeValue> item)
    {
        var attributes = _hasOther ? _names.Length - 1 : _names.Length;
        for (var i = 0; i < attributes; i++)
            _row[i] = item.TryGetValue(_names[i], out var v) ? AttributeText.Display(v) : null;
        if (_hasOther)
        {
            var rest = item.Where(kv => !_known.Contains(kv.Key)).ToList();
            _row[attributes] = rest.Count == 0 ? null : AttributeText.Json(rest);
        }
    }

    public override int FieldCount => _names.Length;
    public override string GetName(int ordinal) => _names[ordinal];
    public override int GetOrdinal(string name) => _ordinal.TryGetValue(name, out var i) ? i : throw new IndexOutOfRangeException(name);
    /// <summary>S, N, BOOL, M, L… as DynamoDB names them; the provider's GenericTypeOf reads these.</summary>
    public override string GetDataTypeName(int ordinal) => _types[ordinal];
    /// <summary>Every column is text: an attribute may be a number in one item and a string in the next.</summary>
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override object GetValue(int ordinal) => (object?)_row[ordinal] ?? DBNull.Value;
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, _row.Length);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }
    public override bool IsDBNull(int ordinal) => _row[ordinal] is null;
    public override string GetString(int ordinal) => _row[ordinal] ?? throw new InvalidCastException("The value is NULL.");
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <summary>The status line's headline instead of "3 rows" or "Done": "Queued" for a write held for the transaction.</summary>
    public string? NoteHeadline => _headline;

    /// <summary>What the connection had to say, then the capacity all the pages used: "4.5 read units" (contract v17).</summary>
    public string? NoteDetail => string.Join(" · ", new[] { _detail, Capacity.Describe(_units, _unitWord) }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } s ? s : null;

    public override bool HasRows => _page.Count > 0 || _next is not null;
    public override bool IsClosed => _closed;
    /// <summary>DynamoDB doesn't say how many items a write touched.</summary>
    public override int RecordsAffected => -1;
    public override int Depth => 0;
    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
    public override void Close() => _closed = true;
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override bool GetBoolean(int ordinal) => bool.Parse(GetString(ordinal));
    public override byte GetByte(int ordinal) => throw new InvalidCastException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new InvalidCastException();
    public override char GetChar(int ordinal) => throw new InvalidCastException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new InvalidCastException();
    public override DateTime GetDateTime(int ordinal) => throw new InvalidCastException();
    public override decimal GetDecimal(int ordinal) => decimal.Parse(GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    public override double GetDouble(int ordinal) => double.Parse(GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    public override short GetInt16(int ordinal) => short.Parse(GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    public override int GetInt32(int ordinal) => int.Parse(GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    public override long GetInt64(int ordinal) => long.Parse(GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Read and write capacity as the status line says it.</summary>
internal static class Capacity
{
    /// <summary>"4.5 read units", "1 write unit"; null when DynamoDB didn't say (DynamoDB Local never does).</summary>
    public static string? Describe(double? units, string word) => units is { } u
        ? $"{u.ToString(u % 1 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)} {word} unit{(u == 1 ? "" : "s")}"
        : null;
}
