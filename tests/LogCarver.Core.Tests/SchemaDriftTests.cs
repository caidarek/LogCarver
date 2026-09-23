using LogCarver.Core;
using Xunit;

namespace LogCarver.Core.Tests;

public class SchemaDriftTests
{
    private static readonly IReadOnlyList<ColumnSchema> Schema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("ColA", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 50, SystemTypeId: 167),
        new ColumnSchema("ColB", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: 50, SystemTypeId: 167),
    ];

    // Real hex (LogTestTrailNull, Id=1) - see RowDecoderTests for provenance.
    private static readonly byte[] RowBytes = Convert.FromHexString(
        "30000800010000000300000200190027006861732D626F7468747261696C696E672D76616C7565");

    [Fact]
    public void Decode_RecordAfterAllBoundaries_Succeeds()
    {
        string[] boundaries = ["0000002D:00000100:0001"];
        var result = RowDecoder.Decode(RowBytes, Schema, recordLsn: "0000002D:00000200:0001", boundaries);
        Assert.Equal(1, result["Id"]);
    }

    [Fact]
    public void Decode_RecordBeforeABoundary_ThrowsSchemaDriftException()
    {
        string[] boundaries = ["0000002D:00000100:0001"];
        var ex = Assert.Throws<SchemaDriftException>(
            () => RowDecoder.Decode(RowBytes, Schema, recordLsn: "0000002C:00000050:0001", boundaries));

        Assert.Equal("0000002C:00000050:0001", ex.RecordLsn);
        Assert.Equal("0000002D:00000100:0001", ex.BoundaryLsn);
    }

    [Fact]
    public void Decode_NoBoundaries_NeverRefuses()
    {
        var result = RowDecoder.Decode(RowBytes, Schema, recordLsn: "0000002C:00000050:0001", ddlBoundaryLsns: []);
        Assert.Equal(1, result["Id"]);
    }
}
