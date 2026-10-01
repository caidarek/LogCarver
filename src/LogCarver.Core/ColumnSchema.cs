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
/// sys.types.system_type_id. Decoded: 56 (int), 127 (bigint), 48 (tinyint),
/// 52 (smallint), 104 (bit), 36 (uniqueidentifier), 40 (date),
/// 42 (datetime2), 61 (datetime), 58 (smalldatetime), 106/108
/// (decimal/numeric), 60 (money), 122 (smallmoney), 62 (float), 59 (real),
/// 167 (varchar), 231 (nvarchar), 175 (char), 239 (nchar).
/// </param>
/// <param name="Scale">
/// sys.columns.scale. Meaningful for decimal/numeric (digits after this
/// many trailing decimal digits are places) AND datetime2 (fractional-
/// seconds precision, which determines both the in-row time part's byte
/// width and the unit its raw integer counts - see
/// <see cref="RowDecoder"/>'s DecodeDateTime2). 0/unused for every other
/// supported type. Always populate this from the real column even for a
/// hand-constructed schema (tests, alternate schema sources) - omitting it
/// for a datetime2 column silently reintroduces a real bug class (values
/// off by a power of 10) rather than failing loudly.
/// </param>
public sealed record ColumnSchema(
    string Name,
    int ColumnId,
    int LeafOffset,
    int LeafNullBit,
    int MaxLength,
    int SystemTypeId,
    int Scale = 0);
