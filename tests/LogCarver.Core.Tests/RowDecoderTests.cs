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
}
