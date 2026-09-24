using System.Text;

namespace LogCarver.Core;

/// <summary>
/// Decodes a SQL Server data row image (the raw bytes captured in
/// fn_dblog's "RowLog Contents 0/1", or read directly from a data page)
/// back into column values.
///
/// Row layout (verified byte-for-byte against a real INSERT record - see
/// SQL Server Log Parser 研究紀錄.txt 第八節):
///   byte 0      TagA (bit 0x10 = has null bitmap, bit 0x20 = has variable-length columns)
///   byte 1      TagB
///   byte 2-3    end offset of the fixed-length region (uint16 LE, includes the 4-byte header)
///   byte 4..    fixed-length columns, positioned by ColumnSchema.LeafOffset
///   +2 bytes    column count (the row's OWN count at write time - may be
///               fewer than the current schema's column count if columns
///               were added since; see the schema-drift warning below)
///   +ceil(colCount/8) bytes   null bitmap (bit = LeafNullBit - 1, 1 = NULL)
///   if has variable-length columns:
///     +2 bytes                variable-length column count (also the row's own count)
///     +N*2 bytes               cumulative end-offset array (uint16 LE)
///     ...                      variable-length data
///
/// IMPORTANT - schema drift: this decoder trusts the row's own embedded
/// column count, not the schema's column count, when sizing the null
/// bitmap and variable-length offset array. That prevents it from reading
/// past the end of an old row. It does NOT protect against decoding a
/// fixed-length column that was added after the row was written - a
/// caller must independently confirm the row's LSN does not predate a
/// schema-changing DDL (see the DDL boundary detection notes in the
/// research log) before trusting any fixed-length column's value.
/// </summary>
public static class RowDecoder
{
    private const int TypeInt = 56;
    private const int TypeDateTime2 = 42;
    private const int TypeChar = 175;
    private const int TypeNVarChar = 231;
    private const int TypeNChar = 239;

    /// <summary>
    /// Decodes a row, first refusing (via <see cref="SchemaDriftException"/>)
    /// if <paramref name="recordLsn"/> predates any LSN in
    /// <paramref name="ddlBoundaryLsns"/>. Boundaries come from
    /// <c>LogCarver.Core.SqlServer.DdlBoundaryReader</c>; this overload
    /// takes plain LSN strings to keep the decoder itself independent of
    /// any SQL Server connectivity type.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Decode(
        ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema,
        string recordLsn, IReadOnlyList<string> ddlBoundaryLsns)
    {
        foreach (var boundaryLsn in ddlBoundaryLsns)
        {
            if (string.CompareOrdinal(recordLsn, boundaryLsn) < 0)
                throw new SchemaDriftException(recordLsn, boundaryLsn);
        }
        return Decode(row, schema);
    }

    public static IReadOnlyDictionary<string, object?> Decode(ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema)
    {
        try
        {
            return DecodeCore(row, schema);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // The most common real cause: an off-row LOB value. Its in-row
            // bytes are a pointer structure, not length-prefixed character
            // data, so treating it as a normal variable-length column
            // computes a length that runs past the end of the row (研究紀錄
            // LOB 小節). Could also mean a compressed row reached here
            // despite the Cli's schema-level compression check. Either way,
            // this is a known-unsupported row format, not a bug to chase -
            // surface it as such rather than a raw indexing exception.
            throw new UnsupportedRowFormatException(
                "Row bytes don't fit the supported layout - likely an off-row LOB value or a compressed row, neither of which this decoder handles yet.",
                ex);
        }
    }

    private static IReadOnlyDictionary<string, object?> DecodeCore(ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema)
    {
        byte tagA = row[0];
        bool hasVarCols = (tagA & 0x20) != 0;
        int fixedEnd = ReadUInt16LE(row, 2);

        var result = new Dictionary<string, object?>();

        foreach (var col in schema)
        {
            if (col.LeafOffset < 0) continue;
            result[col.Name] = col.SystemTypeId switch
            {
                TypeInt => BitConverter.ToInt32(row.Slice(col.LeafOffset, 4)),
                TypeDateTime2 => DecodeDateTime2(row, col.LeafOffset, col.MaxLength),
                // char/nchar are fixed-length in-row, always stored padded
                // with spaces (0x20 / U+0020) out to the declared length -
                // decoded as-is, without trimming, to match what a live
                // SELECT of the column actually returns.
                TypeChar => Windows1252GetString(row.Slice(col.LeafOffset, col.MaxLength)),
                TypeNChar => Encoding.Unicode.GetString(row.Slice(col.LeafOffset, col.MaxLength)),
                _ => throw new NotSupportedException(
                    $"Column '{col.Name}': system_type_id {col.SystemTypeId} is not implemented yet."),
            };
        }

        int colCount = ReadUInt16LE(row, fixedEnd);
        int nullBitmapBytes = (colCount + 7) / 8;
        int nullBitmapStart = fixedEnd + 2;
        int afterNullBitmap = nullBitmapStart + nullBitmapBytes;

        // Fixed-length columns: bytes were decoded above regardless of
        // null-ness (a NULL fixed-length column's bytes are meaningless),
        // now overwrite with null where the bitmap says so.
        foreach (var col in schema)
        {
            if (col.LeafOffset < 0) continue;
            if (IsColNull(row, nullBitmapStart, col.LeafNullBit)) result[col.Name] = null;
        }

        if (hasVarCols)
        {
            // -1 = 1st variable-length column, -2 = 2nd, ... so ordering by
            // LeafOffset descending gives declaration order.
            var varCols = schema.Where(c => c.LeafOffset < 0)
                                 .OrderByDescending(c => c.LeafOffset)
                                 .ToList();
            int varColCount = ReadUInt16LE(row, afterNullBitmap);
            int offsetArrayStart = afterNullBitmap + 2;
            int dataStart = offsetArrayStart + varColCount * 2;

            int prevEnd = dataStart;
            for (int i = 0; i < varColCount; i++)
            {
                ColumnSchema? col = i < varCols.Count ? varCols[i] : null;
                int endOffset = ReadUInt16LE(row, offsetArrayStart + i * 2);
                int len = endOffset - prevEnd;

                if (col is not null)
                {
                    bool isNull = IsColNull(row, nullBitmapStart, col.LeafNullBit);
                    result[col.Name] = isNull
                        ? null
                        : len > 0
                            ? DecodeVarCharBytes(row.Slice(prevEnd, len), col.SystemTypeId)
                            : string.Empty; // len==0 and not null = legitimate empty string, not NULL
                }
                prevEnd = endOffset;
            }

            // Trailing variable-length columns omitted entirely from the row
            // (SQL Server drops the offset-array entry for a NULL column
            // that's last in declaration order) - verified in 研究紀錄 第九節.
            for (int i = varColCount; i < varCols.Count; i++)
            {
                result[varCols[i].Name] = null;
            }
        }

        return result;
    }

    private static int ReadUInt16LE(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.ToUInt16(bytes.Slice(offset, 2));

    private static bool IsColNull(ReadOnlySpan<byte> row, int nullBitmapStart, int nullBit)
    {
        if (nullBit <= 0) return false;
        int bitIndex = nullBit - 1;
        int byteIdx = bitIndex / 8;
        int bitInByte = bitIndex % 8;
        byte byteVal = row[nullBitmapStart + byteIdx];
        return ((byteVal >> bitInByte) & 1) != 0;
    }

    private static DateTime DecodeDateTime2(ReadOnlySpan<byte> row, int offset, int length)
    {
        int timeLen = length - 3;
        if (timeLen != 4)
        {
            // Only DATETIME2(3)/(4) (7-byte storage, 4-byte time part) has
            // been validated against real SQL Server output. Other scales
            // use a different time-part width; refuse rather than guess.
            throw new NotSupportedException(
                $"DATETIME2 storage length {length} is not validated; refusing to decode.");
        }

        uint ticks = BitConverter.ToUInt32(row.Slice(offset, 4)); // milliseconds since midnight

        Span<byte> dateBuf = stackalloc byte[4];
        row.Slice(offset + timeLen, 3).CopyTo(dateBuf);
        uint days = BitConverter.ToUInt32(dateBuf); // days since 0001-01-01

        return new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .AddDays(days)
            .AddMilliseconds(ticks);
    }

    private static readonly Encoding Windows1252Encoding = CreateWindows1252();

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static string Windows1252GetString(ReadOnlySpan<byte> bytes) =>
        Windows1252Encoding.GetString(bytes);

    /// <summary>
    /// nvarchar stores UTF-16LE (2 bytes/char) in-row, unlike varchar's
    /// single-byte encoding. Decoding one as the other silently produces
    /// plausible-looking garbage (e.g. "first" -> "f i r s t", each letter
    /// followed by a stray null byte read as a control character) with no
    /// error - caught via a real nvarchar column while verifying the
    /// snapshot feature.
    /// </summary>
    private static string DecodeVarCharBytes(ReadOnlySpan<byte> bytes, int systemTypeId) =>
        systemTypeId == TypeNVarChar
            ? Encoding.Unicode.GetString(bytes)
            : Windows1252GetString(bytes);
}
