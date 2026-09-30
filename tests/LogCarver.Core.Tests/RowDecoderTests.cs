using LogCarver.Core;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// These test rows are not synthetic unless noted - the hex was captured
/// from fn_dblog's RowLog Contents 0 against a real SQL Server 2025
/// instance during the research phase (SQL Server Log Parser
/// 研究紀錄.txt 第八、九節) and cross-checked byte-for-byte by hand
/// before being used here as a regression baseline.
/// </summary>
public class RowDecoderTests
{
    // dbo.LogTest: Id INT, CreatedAt DATETIME2(3), Amount INT, Note VARCHAR(200), Sentinel VARCHAR(100)
    private static readonly IReadOnlyList<ColumnSchema> LogTestSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("CreatedAt", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 7, SystemTypeId: 42),
        new ColumnSchema("Amount", 3, LeafOffset: 15, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 167),
        new ColumnSchema("Sentinel", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 100, SystemTypeId: 167),
    ];

    // dbo.LogTestTrailNull: Id INT, ColA VARCHAR(50) NOT NULL, ColB VARCHAR(50) NULL
    private static readonly IReadOnlyList<ColumnSchema> TrailNullSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("ColA", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 50, SystemTypeId: 167),
        new ColumnSchema("ColB", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: 50, SystemTypeId: 167),
    ];

    // dbo.T: Id INT, Name NVARCHAR(50)
    private static readonly IReadOnlyList<ColumnSchema> NvarcharSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Name", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 100, SystemTypeId: 231),
    ];

    // dbo.T2: Id INT, C CHAR(5), NC NCHAR(5)
    private static readonly IReadOnlyList<ColumnSchema> CharSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("C", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 5, SystemTypeId: 175),
        new ColumnSchema("NC", 3, LeafOffset: 13, LeafNullBit: 3, MaxLength: 10, SystemTypeId: 239),
    ];

    // dbo.T3: Id INT, D DATE
    private static readonly IReadOnlyList<ColumnSchema> DateSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("D", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 3, SystemTypeId: 40),
    ];

    // dbo.DecimalTest: Id INT, Small DECIMAL(5,2), Big DECIMAL(18,4)
    private static readonly IReadOnlyList<ColumnSchema> DecimalSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Small", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 5, SystemTypeId: 106, Scale: 2),
        new ColumnSchema("Big", 3, LeafOffset: 13, LeafNullBit: 3, MaxLength: 9, SystemTypeId: 106, Scale: 4),
    ];

    // dbo.LobOffRowTest: Id INT, Note NVARCHAR(MAX), with
    // `EXEC sp_tableoption 'dbo.LobOffRowTest', 'large value types out of row', 1`
    // forcing Note off-row regardless of its actual content length.
    private static readonly IReadOnlyList<ColumnSchema> LobOffRowSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 231),
    ];

    // dbo.LobNotLastTest: Id INT, Note NVARCHAR(MAX) (off-row, forced), Tag NVARCHAR(20)
    // (in-row) - Note is declared before Tag, so it is NOT the last variable-length
    // column, exercising offset-chaining through a masked complex-column entry.
    private static readonly IReadOnlyList<ColumnSchema> LobNotLastSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 231),
        new ColumnSchema("Tag", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: 40, SystemTypeId: 231),
    ];

    // dbo.LobTwoOffRowTest: Id INT, NoteA NVARCHAR(MAX), NoteB NVARCHAR(MAX), both
    // forced off-row - two consecutive complex-column entries in the same row.
    private static readonly IReadOnlyList<ColumnSchema> LobTwoOffRowSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("NoteA", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 231),
        new ColumnSchema("NoteB", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: -1, SystemTypeId: 231),
    ];

    [Fact]
    public void Decode_RealInsertRow_MatchesGroundTruth()
    {
        byte[] row = Convert.FromHexString(
            "3000130001000000F73F6404294A0BE90300000500000200220046006E6F74652D314C50542D" +
            "6261613163613933373934393433656361623638616237376438646365643633");

        var result = RowDecoder.Decode(row, LogTestSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(1001, result["Amount"]);
        Assert.Equal("note-1", result["Note"]);
        Assert.Equal("LPT-baa1ca93794943ecab68ab77d8dced63", result["Sentinel"]);
        Assert.IsType<DateTime>(result["CreatedAt"]);
        Assert.Equal(2026, ((DateTime)result["CreatedAt"]!).Year); // exact ground truth for this field wasn't captured during research
    }

    [Fact]
    public void Decode_TrailingVariableColumnPresent_DecodesNormally()
    {
        byte[] row = Convert.FromHexString(
            "30000800010000000300000200190027006861732D626F7468747261696C696E672D76616C7565");

        var result = RowDecoder.Decode(row, TrailNullSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("has-both", result["ColA"]);
        Assert.Equal("trailing-value", result["ColB"]);
    }

    [Fact]
    public void Decode_TrailingVariableColumnOmitted_DecodesAsNull()
    {
        // ColB is NULL and is the last variable-length column - SQL Server
        // drops its offset-array entry entirely rather than recording a
        // zero-length entry (verified empirically, 研究紀錄 第九節).
        byte[] row = Convert.FromHexString(
            "300008000200000003000401001C00747261696C696E672D6E756C6C");

        var result = RowDecoder.Decode(row, TrailNullSchema);

        Assert.Equal(2, result["Id"]);
        Assert.Equal("trailing-null", result["ColA"]);
        Assert.Null(result["ColB"]);
    }

    [Fact]
    public void Decode_EmptyStringVariableColumn_IsNotConfusedWithNull()
    {
        // Synthetic row (not captured from a real instance): Id=42, A="hi", B="".
        // A zero-length, non-NULL variable column is a real, valid SQL Server
        // value distinct from NULL - the original PowerShell prototype this
        // decoder was ported from conflated the two; this locks in the fix.
        byte[] row = Convert.FromHexString("300008002A0000000300000200130013006869");

        var result = RowDecoder.Decode(row, TrailNullSchema);

        Assert.Equal(42, result["Id"]);
        Assert.Equal("hi", result["ColA"]);
        Assert.Equal(string.Empty, result["ColB"]);
        Assert.NotNull(result["ColB"]);
    }

    [Fact]
    public void Decode_NvarcharColumn_DecodesAsUtf16_NotWindows1252()
    {
        // Real captured row for INSERT INTO dbo.T (Id, Name) VALUES (1, N'hi').
        // nvarchar is stored in-row as UTF-16LE (2 bytes/char), unlike
        // varchar's single byte/char - decoding it as Windows-1252 silently
        // produced "h\0i\0" (each letter followed by a stray control
        // character) with no error. Caught while verifying the snapshot
        // feature against a real nvarchar column.
        byte[] row = Convert.FromHexString("30000800010000000200000100130068006900");

        var result = RowDecoder.Decode(row, NvarcharSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("hi", result["Name"]);
    }

    [Fact]
    public void Decode_CharAndNcharColumns_AreFixedLengthAndSpacePadded()
    {
        // Real captured row for INSERT INTO dbo.T2 (Id, C, NC) VALUES (1, 'ab', N'xy').
        // char/nchar are fixed-length in-row (unlike varchar/nvarchar), always
        // stored padded with spaces out to the declared length - char with
        // single-byte 0x20, nchar with UTF-16 U+0020.
        byte[] row = Convert.FromHexString("1000170001000000616220202078007900200020002000030000");

        var result = RowDecoder.Decode(row, CharSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("ab   ", result["C"]);
        Assert.Equal("xy   ", result["NC"]);
    }

    [Fact]
    public void Decode_DateColumn_DecodesAsDaysSinceYearOne()
    {
        // Synthetic row (not captured from a real instance): Id=42, D=2026-01-15.
        // DATE (system_type_id 40) was entirely unimplemented until this test -
        // every row from a table with a DATE column failed to decode at all
        // (real customer impact: StockDecisionDaily/StockPriceDaily-style
        // tables with a plain date column), which in turn made
        // RowEventExporter.ToSql silently produce an empty file for every
        // event in such a table since UndoSqlGenerator/ReplaySqlGenerator
        // both require a non-null Before/After image to work from.
        //
        // 2026-01-15 is 739630 days after 0001-01-01 (independently computed
        // via .NET's own DateTime subtraction, not via this decoder), stored
        // as the same 3-byte little-endian day count DATETIME2's date part
        // already uses: 739630 = 0x000B492E -> bytes 2E 49 0B.
        byte[] row = Convert.FromHexString("10000B002A0000002E490B020000");

        var result = RowDecoder.Decode(row, DateSchema);

        Assert.Equal(42, result["Id"]);
        Assert.Equal(new DateTime(2026, 1, 15), result["D"]);
    }

    [Fact]
    public void Decode_PositiveDecimalColumns_MatchesGroundTruth()
    {
        // Real captured row for INSERT INTO dbo.DecimalTest (Id, Small, Big)
        // VALUES (1, 123.45, 123456789012.3456), against a real SQL Server
        // instance. DECIMAL/NUMERIC (system_type_id 106/108) was entirely
        // unimplemented until this test - every row from a table with any
        // decimal/numeric column failed to decode at all (real customer
        // impact: DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice columns, found the same day as
        // the DATE gap above - fixing DATE alone was not enough for that
        // table).
        byte[] row = Convert.FromHexString(
            "1000160001000000013930000001C0BA8A3CD5620400030000");

        var result = RowDecoder.Decode(row, DecimalSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(123.45m, result["Small"]);
        Assert.Equal(123456789012.3456m, result["Big"]);
    }

    [Fact]
    public void Decode_NegativeDecimalColumns_SignByteIsInverted()
    {
        // Real captured row for INSERT INTO dbo.DecimalTest (Id, Small, Big)
        // VALUES (2, -123.45, -123456789012.3456) - same magnitude bytes as
        // the positive-value test above, only the sign byte differs (0x00
        // here vs 0x01 there), confirming SQL Server's convention is
        // 1 = positive, 0 = negative (the opposite of the usual sign-bit
        // convention, easy to get backwards without a real captured
        // negative example to check against).
        byte[] row = Convert.FromHexString(
            "1000160002000000003930000000C0BA8A3CD5620400030000");

        var result = RowDecoder.Decode(row, DecimalSchema);

        Assert.Equal(2, result["Id"]);
        Assert.Equal(-123.45m, result["Small"]);
        Assert.Equal(-123456789012.3456m, result["Big"]);
    }

    [Fact]
    public void Decode_OffRowLobColumn_IsMarkedNotDecoded_OtherColumnsUnaffected()
    {
        // Real captured row for INSERT INTO dbo.LobOffRowTest (Id, Note)
        // VALUES (1, N'OFFROW-SENTINEL-1234567890') after sp_tableoption
        // forced Note off-row. The offset-array entry for Note is 0x801F -
        // bit 0x8000 is SQL Server's "complex column" flag marking an
        // off-row/pointer structure (16 bytes, independent of the LOB's
        // actual content length - also verified against a 5000-char value
        // producing the identical shape), not length-prefixed text.
        //
        // Before this fix, treating 0x801F as a plain cumulative offset
        // computed a length that ran past the end of the row and threw,
        // which the outer catch in Decode wrapped as
        // UnsupportedRowFormatException and discarded the ENTIRE row -
        // including the perfectly decodable Id column. Real customer
        // impact: DECISION.StockDecisionDaily's free-text reason/note
        // columns (nvarchar(max)) caused every single row to be silently
        // dropped from Undo/Replay/SQL export (matched 1589601 events, 0
        // bytes of output).
        byte[] row = Convert.FromHexString(
            "30000800010000000200BC01001F8000009165000000008802000001000000");

        var result = RowDecoder.Decode(row, LobOffRowSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("<off-row value, not decoded>", result["Note"]);
    }

    [Fact]
    public void Decode_OffRowLobColumn_NotLastVariableColumn_ChainsOffsetCorrectly()
    {
        // Real captured row for INSERT INTO dbo.LobNotLastTest (Note, Tag)
        // VALUES (N'OFFROW-NOT-LAST-SENTINEL', N'tag-value') - Note (off-row)
        // is declared BEFORE Tag (in-row), unlike the single-column test
        // above. Confirms the masked offset chains correctly into a
        // following real column: without unconditionally using the masked
        // value as prevEnd, Tag's length would be computed from the raw
        // (unmasked, ~32768-biased) offset and misdecode or throw.
        byte[] row = Convert.FromHexString(
            "3000080001000000030000020021803300000094650000000098020000010000007400610067002D00760061006C0075006500");

        var result = RowDecoder.Decode(row, LobNotLastSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("<off-row value, not decoded>", result["Note"]);
        Assert.Equal("tag-value", result["Tag"]);
    }

    [Fact]
    public void Decode_TwoOffRowLobColumns_BothMarkedNotDecoded()
    {
        // Real captured row for INSERT INTO dbo.LobTwoOffRowTest (NoteA, NoteB)
        // VALUES (N'FIRST-OFFROW-SENTINEL', N'SECOND-OFFROW-SENTINEL'), both
        // forced off-row - two consecutive complex-column offset-array
        // entries in the same row, confirming the masked offset chains
        // correctly from one off-row column into the next.
        byte[] row = Convert.FromHexString(
            "30000800010000000300000200218031800000956500000000A8020000010000000000966500000000A802000001000100");

        var result = RowDecoder.Decode(row, LobTwoOffRowSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("<off-row value, not decoded>", result["NoteA"]);
        Assert.Equal("<off-row value, not decoded>", result["NoteB"]);
    }
}
