namespace LogCarver.Core;

/// <summary>
/// One column's physical layout within a row, as reported by
/// sys.system_internals_partition_columns. Query this dynamically per table;
/// never hardcode a type map (see CLAUDE.md).
/// </summary>
/// <param name="Name">Column name.</param>
/// <param name="ColumnId">sys.columns.column_id (declaration order).</param>
/// <param name="LeafOffset">
/// &gt;= 0: byte offset of this fixed-length column within the row.
/// &lt; 0: this is the Nth variable-length column, in declaration order
/// (-1 = 1st, -2 = 2nd, ...).
/// </param>
/// <param name="LeafNullBit">
/// 1-based position of this column's bit in the row's null bitmap.
/// A gap in this sequence (e.g. 2 -&gt; 4) means a column was dropped or
/// type-changed between the two positions.
/// </param>
/// <param name="MaxLength">Declared max length in bytes.</param>
/// <param name="SystemTypeId">
/// sys.types.system_type_id. Only 56 (int), 42 (datetime2), 167 (varchar),
/// 231 (nvarchar), 175 (char), 239 (nchar) are currently decoded.
/// </param>
public sealed record ColumnSchema(
    string Name,
    int ColumnId,
    int LeafOffset,
    int LeafNullBit,
    int MaxLength,
    int SystemTypeId);
