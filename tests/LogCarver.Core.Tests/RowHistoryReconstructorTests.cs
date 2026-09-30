using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class RowHistoryReconstructorTests
{
    private static readonly IReadOnlyList<ColumnSchema> LogTestSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("CreatedAt", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 7, SystemTypeId: 42, Scale: 3),
        new ColumnSchema("Amount", 3, LeafOffset: 15, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 167),
        new ColumnSchema("Sentinel", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 100, SystemTypeId: 167),
    ];

    // Real hex: Id=1 insert (see RowPatcherTests for provenance), same table.
    private static readonly byte[] InsertRow = Convert.FromHexString(
        "3000130001000000F73F6404294A0BE90300000500000200220046006E6F74652D314C50542D" +
        "6261613163613933373934393433656361623638616237376438646365643633");
    private static readonly byte[] Rlc0 = Convert.FromHexString("E903"); // Amount 1001
    private static readonly byte[] Rlc1 = Convert.FromHexString("2923"); // Amount 9001

    private static LogRecord Insert(string lsn, string pageId, int slotId, byte[] bytes) =>
        new(lsn, "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.LogTest.pk", pageId, slotId, null, bytes, null);

    private static LogRecord Delete(string lsn, string pageId, int slotId, byte[] bytes) =>
        new(lsn, "LOP_DELETE_ROWS", "LCX_MARK_AS_GHOST", null, "dbo.LogTest.pk", pageId, slotId, null, bytes, null);

    private static LogRecord Update(string lsn, string pageId, int slotId, int offset, byte[] rlc0, byte[] rlc1) =>
        new(lsn, "LOP_MODIFY_ROW", "LCX_CLUSTERED", offset, "dbo.LogTest.pk", pageId, slotId, null, rlc0, rlc1);

    // Real captured LOP_MODIFY_COLUMNS RowLogContents0/1 (a column-level
    // change descriptor, NOT a byte-range splice - see the operation's own
    // case in RowHistoryReconstructor.Reconstruct). Content doesn't matter
    // for these tests since the point is that it's refused outright rather
    // than fed into RowPatcher, but using real bytes rather than made-up
    // ones costs nothing.
    private static LogRecord ModifyColumns(string lsn, string pageId, int slotId) =>
        new(lsn, "LOP_MODIFY_COLUMNS", "LCX_CLUSTERED", 0, "dbo.LogTest.pk", pageId, slotId, null,
            Convert.FromHexString("0F000F002E002E00"), Convert.FromHexString("01000100"));

    [Fact]
    public void InsertThenUpdate_ReconstructsBeforeAndAfterFromInsertImage()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            Update("00000002", "0001:0F", 3, offset: 15, Rlc0, Rlc1),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        Assert.Equal(RowEventKind.Insert, history[0].Kind);
        Assert.Equal(1001, history[0].After!["Amount"]);

        var update = history[1];
        Assert.Equal(RowEventKind.Update, update.Kind);
        Assert.Equal(1001, update.Before!["Amount"]);
        Assert.Equal(9001, update.After!["Amount"]);
        Assert.Equal("note-1", update.After!["Note"]); // untouched column carried through correctly
    }

    [Fact]
    public void UpdateWithNoPriorInsertObserved_ReportsUnknownRatherThanGuessing()
    {
        LogRecord[] records = [Update("00000001", "0001:0F", 3, offset: 15, Rlc0, Rlc1)];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Single(history);
        Assert.Null(history[0].Before);
        Assert.Null(history[0].After);
        Assert.Contains("before image unknown", history[0].Note);
    }

    [Fact]
    public void UpdateAfterDelete_BeforeAnyNewInsertReusesTheSlot_ReportsUnknownRatherThanStaleRow()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            Delete("00000002", "0001:0F", 3, InsertRow),
            // No new INSERT reuses this slot yet. If an UPDATE record still
            // shows up against it, the deleted row's bytes must not be
            // reused as a stale "before" image.
            Update("00000003", "0001:0F", 3, offset: 15, Rlc0, Rlc1),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(3, history.Count);
        var update = history[2];
        Assert.Null(update.Before);
        Assert.Null(update.After);
        Assert.Contains("before image unknown", update.Note);
    }

    [Fact]
    public void ModifyColumns_RefusedOutright_NeverFedToRowPatcher()
    {
        // Regression test for a real bug: an earlier version of this method
        // grouped LOP_MODIFY_COLUMNS with LOP_MODIFY_ROW and spliced its
        // RowLogContents via RowPatcher, which doesn't throw - it silently
        // produces a corrupted row that decodes to garbage (observed
        // against a real captured record: a garbage Id and the other
        // columns missing entirely, with no error or Note at all).
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            ModifyColumns("00000002", "0001:0F", 3),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        var modifyColumnsEvent = history[1];
        Assert.Equal(RowEventKind.Update, modifyColumnsEvent.Kind);
        Assert.Null(modifyColumnsEvent.Before);
        Assert.Null(modifyColumnsEvent.After);
        Assert.Contains("LOP_MODIFY_COLUMNS", modifyColumnsEvent.Note);
    }

    [Fact]
    public void ModifyColumns_ForgetsTheSlotsKnownImage_SoALaterModifyRowDoesNotSpliceAgainstStaleBytes()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            ModifyColumns("00000002", "0001:0F", 3),
            // This row's real current bytes are now unknown - a LOP_MODIFY_ROW
            // splice against the pre-LOP_MODIFY_COLUMNS image would silently
            // produce a plausible-looking but wrong "after" row.
            Update("00000003", "0001:0F", 3, offset: 15, Rlc0, Rlc1),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(3, history.Count);
        var laterUpdate = history[2];
        Assert.Null(laterUpdate.Before);
        Assert.Null(laterUpdate.After);
        Assert.Contains("before image unknown", laterUpdate.Note);
    }
}
