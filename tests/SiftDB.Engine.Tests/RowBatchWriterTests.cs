using System.Buffers.Binary;
using System.Data;
using System.Text;
using SiftDB.Bridge;

namespace SiftDB.Engine.Tests;

/// <summary>NULL handling in the binary batch format: a bitmap per column that has any NULL.</summary>
public class RowBatchWriterTests
{
    private const int Entry = 20;

    private static DataTable Table()
    {
        var t = new DataTable();
        t.Columns.Add("i", typeof(int));
        t.Columns.Add("big", typeof(long));
        t.Columns.Add("r", typeof(double));
        t.Columns.Add("s", typeof(string));
        return t;
    }

    private static byte[] Encode(DataTable t, out RowBatchWriter writer)
    {
        using var reader = t.CreateDataReader();
        writer = new RowBatchWriter(reader);
        while (reader.Read()) writer.AppendRow(reader);
        return writer.Flush();
    }

    private static (uint Kind, int Off, int Len, int NullOff, int NullLen) Col(byte[] d, int c)
    {
        var e = 12 + c * Entry;
        return (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e)), (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 8)), (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 12)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 16)));
    }

    private static bool IsNull(byte[] d, int c, int row)
    {
        var col = Col(d, c);
        return col.NullLen > 0 && (d[col.NullOff + (row >> 3)] & (1 << (row & 7))) != 0;
    }

    [Fact]
    public void A_column_without_NULLs_carries_no_bitmap()
    {
        var t = Table();
        t.Rows.Add(1, 2L, 3.0, "a");
        t.Rows.Add(4, 5L, 6.0, "b");
        var d = Encode(t, out _);
        for (var c = 0; c < 4; c++)
        {
            Assert.Equal(0, Col(d, c).NullLen);
            Assert.Equal(0, Col(d, c).NullOff);
        }
    }

    [Fact]
    public void NULLs_are_flagged_for_every_column_kind_and_only_on_their_rows()
    {
        var t = Table();
        t.Rows.Add(1, 10L, 1.5, "a");
        t.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        t.Rows.Add(3, 30L, 3.5, "c");
        var d = Encode(t, out _);

        for (var c = 0; c < 4; c++)
        {
            Assert.False(IsNull(d, c, 0));
            Assert.True(IsNull(d, c, 1));
            Assert.False(IsNull(d, c, 2));
        }
        // Data under a NULL is a well-formed zero / empty value.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Col(d, 0).Off + 4)));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(Col(d, 1).Off + 8)));
        Assert.Equal(0.0, BitConverter.ToDouble(d, Col(d, 2).Off + 8));
    }

    [Fact]
    public void NULL_and_the_empty_string_are_different()
    {
        var t = Table();
        t.Rows.Add(1, 1L, 1.0, "");
        t.Rows.Add(2, 2L, 2.0, DBNull.Value);
        t.Rows.Add(3, 3L, 3.0, "x");
        var d = Encode(t, out _);

        Assert.False(IsNull(d, 3, 0)); // empty string, not NULL
        Assert.True(IsNull(d, 3, 1));
        Assert.False(IsNull(d, 3, 2));

        var so = Col(d, 3).Off;
        int Str(int i) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(so + i * 4));
        Assert.Equal(0, Str(1) - Str(0));
        Assert.Equal(0, Str(2) - Str(1));
        Assert.Equal("x", Encoding.UTF8.GetString(d, so + 4 * 4 + Str(2), Str(3) - Str(2)));
    }

    [Fact]
    public void Only_the_columns_that_have_a_NULL_get_a_bitmap()
    {
        var t = Table();
        t.Rows.Add(1, 1L, 1.0, "a");
        t.Rows.Add(2, DBNull.Value, 2.0, "b");
        var d = Encode(t, out _);
        Assert.Equal(0, Col(d, 0).NullLen);
        Assert.Equal(1, Col(d, 1).NullLen);
        Assert.Equal(0, Col(d, 2).NullLen);
        Assert.Equal(0, Col(d, 3).NullLen);
    }

    [Fact]
    public void Bitmaps_and_columns_are_8_byte_aligned()
    {
        var t = Table();
        for (var i = 0; i < 13; i++) t.Rows.Add(i, i, i, i % 2 == 0 ? DBNull.Value : "v");
        var d = Encode(t, out _);
        for (var c = 0; c < 4; c++)
        {
            var col = Col(d, c);
            Assert.Equal(0, col.Off % 8);
            if (col.NullLen > 0) Assert.Equal(0, col.NullOff % 8);
        }
        Assert.Equal(0, d.Length % 8);
    }

    [Fact]
    public void A_NULL_in_one_batch_does_not_leak_into_the_next()
    {
        var t = Table();
        t.Rows.Add(DBNull.Value, 1L, 1.0, "a");
        using var reader = t.CreateDataReader();
        var writer = new RowBatchWriter(reader);
        reader.Read();
        writer.AppendRow(reader);
        var first = writer.Flush();
        Assert.True(IsNull(first, 0, 0));

        var t2 = Table();
        t2.Rows.Add(7, 7L, 7.0, "z");
        using var reader2 = t2.CreateDataReader();
        reader2.Read();
        writer.AppendRow(reader2); // same writer, reused
        var second = writer.Flush();
        Assert.Equal(0, Col(second, 0).NullLen);
        Assert.False(IsNull(second, 0, 0));
    }

    [Fact]
    public void NULLs_survive_the_writer_growing_past_its_initial_capacity()
    {
        var t = Table();
        const int rows = 5000; // initial capacity is 1024: forces several growths
        for (var i = 0; i < rows; i++)
            t.Rows.Add(i % 3 == 0 ? DBNull.Value : i, 1L, 1.0, "s");
        var d = Encode(t, out var w);

        Assert.Equal((uint)rows, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)));
        Assert.Equal((rows + 7) / 8, Col(d, 0).NullLen);
        for (var i = 0; i < rows; i++) Assert.Equal(i % 3 == 0, IsNull(d, 0, i));
    }

    [Fact]
    public void A_NULL_first_seen_after_the_writer_has_grown_is_still_recorded()
    {
        var t = Table();
        for (var i = 0; i < 3000; i++) t.Rows.Add(i, 1L, 1.0, "s");
        t.Rows.Add(DBNull.Value, 1L, 1.0, "s"); // first NULL, long after the initial capacity
        var d = Encode(t, out _);
        Assert.True(IsNull(d, 0, 3000));
        Assert.False(IsNull(d, 0, 2999));
    }
}

/// <summary>How non-string values read in the grid: dates and times as ISO, whatever the machine's culture.</summary>
public class FormatValueTests
{
    [Fact]
    public void Dates_and_times_are_ISO()
    {
        Assert.Equal("2026-11-12", SiftDB.Bridge.RowBatchWriter.FormatValue(new DateTime(2026, 11, 12), dateOnly: true));
        Assert.Equal("2026-11-12 00:00:00", SiftDB.Bridge.RowBatchWriter.FormatValue(new DateTime(2026, 11, 12)));   // a timestamp keeps its time
        Assert.Equal("2026-11-12 14:05:09.5", SiftDB.Bridge.RowBatchWriter.FormatValue(new DateTime(2026, 11, 12, 14, 5, 9, 500)));
        Assert.Equal("2026-11-12 14:05:09Z", SiftDB.Bridge.RowBatchWriter.FormatValue(new DateTime(2026, 11, 12, 14, 5, 9, DateTimeKind.Utc)));
        Assert.Equal("2026-11-12", SiftDB.Bridge.RowBatchWriter.FormatValue(new DateOnly(2026, 11, 12)));
        Assert.Equal("14:05:09", SiftDB.Bridge.RowBatchWriter.FormatValue(new TimeOnly(14, 5, 9)));
        Assert.Equal("1.5", SiftDB.Bridge.RowBatchWriter.FormatValue(1.5m));
    }
}
