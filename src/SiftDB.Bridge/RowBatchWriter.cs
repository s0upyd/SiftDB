using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SiftDB.Bridge;

/// <summary>
/// Accumulates reader rows into columnar buffers and encodes them as one binary batch.
///
/// Layout (little-endian):
///   u32 magic "SFTB" | u32 rowCount | u32 colCount
///   colCount x { u32 kind, u32 offset, u32 length }   (offsets absolute, 8-byte aligned)
///   column data:
///     Int32   rowCount x i32
///     Int64   rowCount x i64
///     Float64 rowCount x f64
///     Text    (rowCount+1) x i32 offsets, then UTF-8 bytes
/// Spike limitation: NULLs are encoded as 0 / empty string.
/// </summary>
public sealed class RowBatchWriter
{
    public const uint Magic = 0x42544653; // "SFTB" read as little-endian u32

    private sealed class Col
    {
        public ColumnKind Kind;
        public Type Source = typeof(string);
        public int[] I = [];
        public long[] L = [];
        public double[] D = [];
        public byte[] B = [];
        public int[] Off = [];
        public int BLen;
    }

    private readonly Col[] _cols;
    private int _capacity;

    public IReadOnlyList<ColumnDescriptor> Columns { get; }
    public int RowCount { get; private set; }
    public int ApproxBytes { get; private set; }

    public RowBatchWriter(DbDataReader reader)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException();
        var n = reader.FieldCount;
        _cols = new Col[n];
        var descs = new ColumnDescriptor[n];
        for (var i = 0; i < n; i++)
        {
            var t = reader.GetFieldType(i);
            var kind = KindFor(t);
            _cols[i] = new Col { Kind = kind, Source = t };
            descs[i] = new ColumnDescriptor(reader.GetName(i), kind);
        }
        Columns = descs;
        Grow(1024);
    }

    public static ColumnKind KindFor(Type t)
    {
        if (t == typeof(short) || t == typeof(int) || t == typeof(byte)) return ColumnKind.Int32;
        if (t == typeof(long)) return ColumnKind.Int64;
        if (t == typeof(float) || t == typeof(double)) return ColumnKind.Float64;
        return ColumnKind.Text;
    }

    public void AppendRow(DbDataReader r)
    {
        if (RowCount == _capacity) Grow(_capacity * 2);
        var row = RowCount;
        for (var c = 0; c < _cols.Length; c++)
        {
            var col = _cols[c];
            var isNull = r.IsDBNull(c);
            switch (col.Kind)
            {
                case ColumnKind.Int32:
                    col.I[row] = isNull ? 0 : col.Source == typeof(int) ? r.GetInt32(c)
                        : col.Source == typeof(short) ? r.GetInt16(c) : r.GetByte(c);
                    ApproxBytes += 4;
                    break;
                case ColumnKind.Int64:
                    col.L[row] = isNull ? 0 : r.GetInt64(c);
                    ApproxBytes += 8;
                    break;
                case ColumnKind.Float64:
                    col.D[row] = isNull ? 0 : col.Source == typeof(double) ? r.GetDouble(c) : r.GetFloat(c);
                    ApproxBytes += 8;
                    break;
                default:
                    if (!isNull)
                    {
                        var s = col.Source == typeof(string)
                            ? r.GetString(c)
                            : Convert.ToString(r.GetValue(c), CultureInfo.InvariantCulture) ?? "";
                        var need = Encoding.UTF8.GetByteCount(s);
                        if (col.BLen + need > col.B.Length)
                            Array.Resize(ref col.B, Math.Max(col.B.Length * 2, col.BLen + need));
                        Encoding.UTF8.GetBytes(s, 0, s.Length, col.B, col.BLen);
                        col.BLen += need;
                        ApproxBytes += need;
                    }
                    col.Off[row + 1] = col.BLen;
                    ApproxBytes += 4;
                    break;
            }
        }
        RowCount++;
    }

    /// <summary>Encodes the accumulated rows into a new array and clears the writer for reuse.</summary>
    public byte[] Flush()
    {
        var n = RowCount;
        var pos = Align8(12 + _cols.Length * 12);
        var offsets = new int[_cols.Length];
        var lengths = new int[_cols.Length];
        for (var c = 0; c < _cols.Length; c++)
        {
            var col = _cols[c];
            lengths[c] = col.Kind switch
            {
                ColumnKind.Int32 => n * 4,
                ColumnKind.Int64 or ColumnKind.Float64 => n * 8,
                _ => (n + 1) * 4 + col.BLen,
            };
            offsets[c] = pos;
            pos = Align8(pos + lengths[c]);
        }

        var buf = new byte[pos];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)_cols.Length);
        for (var c = 0; c < _cols.Length; c++)
        {
            var col = _cols[c];
            var e = 12 + c * 12;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e), (uint)col.Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e + 4), (uint)offsets[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e + 8), (uint)lengths[c]);
            var dest = buf.AsSpan(offsets[c], lengths[c]);
            switch (col.Kind)
            {
                case ColumnKind.Int32: MemoryMarshal.AsBytes(col.I.AsSpan(0, n)).CopyTo(dest); break;
                case ColumnKind.Int64: MemoryMarshal.AsBytes(col.L.AsSpan(0, n)).CopyTo(dest); break;
                case ColumnKind.Float64: MemoryMarshal.AsBytes(col.D.AsSpan(0, n)).CopyTo(dest); break;
                default:
                    MemoryMarshal.AsBytes(col.Off.AsSpan(0, n + 1)).CopyTo(dest);
                    col.B.AsSpan(0, col.BLen).CopyTo(dest[((n + 1) * 4)..]);
                    col.BLen = 0;
                    break;
            }
        }

        RowCount = 0;
        ApproxBytes = 0;
        return buf;
    }

    private static int Align8(int v) => (v + 7) & ~7;

    private void Grow(int capacity)
    {
        foreach (var col in _cols)
        {
            switch (col.Kind)
            {
                case ColumnKind.Int32: Array.Resize(ref col.I, capacity); break;
                case ColumnKind.Int64: Array.Resize(ref col.L, capacity); break;
                case ColumnKind.Float64: Array.Resize(ref col.D, capacity); break;
                default:
                    Array.Resize(ref col.Off, capacity + 1);
                    if (col.B.Length == 0) col.B = new byte[4096];
                    break;
            }
        }
        _capacity = capacity;
    }
}
