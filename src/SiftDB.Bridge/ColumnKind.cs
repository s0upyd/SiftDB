namespace SiftDB.Bridge;

/// <summary>Wire type of a column inside a row batch.</summary>
public enum ColumnKind : byte
{
    Text = 0,
    Int32 = 1,
    Int64 = 2,
    Float64 = 3,
}

public sealed record ColumnDescriptor(string Name, ColumnKind Kind);
