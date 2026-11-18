using System.Data.Common;
using System.Reflection;
using Npgsql;

namespace SiftDB.Providers.Postgres;

/// <summary>
/// Which table and column each result column comes from (design section 14, F4). Postgres says so for every result, in
/// the RowDescription message: each field's table oid and column number (0 for a computed column). Npgsql keeps that
/// message on an internal field and offers it publicly only through <c>GetColumnSchema()</c>, which runs a catalog query
/// on a second connection for every result: far too dear to ask for each statement. So it is read by reflection, here
/// and nowhere else, the way <see cref="PostgresTransactionMonitor"/> reads the protocol status. If a later Npgsql moves
/// it, <see cref="Available"/> turns false and results simply have no links; a test fails loudly when that happens.
/// </summary>
internal static class PostgresRowDescription
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? Message = typeof(NpgsqlDataReader).GetField("RowDescription", Any);
    private static readonly PropertyInfo? Item = Message?.FieldType.GetProperties(Any)
        .FirstOrDefault(p => p.GetIndexParameters() is [{ ParameterType: var t }] && t == typeof(int));
    private static readonly PropertyInfo? Count = Message?.FieldType.GetProperty("Count", Any);
    private static readonly PropertyInfo? TableOid = Item?.PropertyType.GetProperty("TableOID", Any);
    private static readonly PropertyInfo? ColumnNumber = Item?.PropertyType.GetProperty("ColumnAttributeNumber", Any);

    /// <summary>False if this Npgsql no longer offers the row description where this looks for it.</summary>
    public static bool Available => Item is not null && Count is not null && TableOid is not null && ColumnNumber is not null;

    /// <summary>Each column's table oid and column number (0, 0 when computed), or null when it cannot be read.</summary>
    public static (uint Table, short Column)[]? Read(DbDataReader reader)
    {
        if (!Available || reader is not NpgsqlDataReader npgsql || Message!.GetValue(npgsql) is not { } message) return null;
        var n = (int)Count!.GetValue(message)!;
        var result = new (uint, short)[n];
        for (var i = 0; i < n; i++)
        {
            var field = Item!.GetValue(message, [i])!;
            result[i] = ((uint)TableOid!.GetValue(field)!, (short)ColumnNumber!.GetValue(field)!);
        }
        return result;
    }
}
