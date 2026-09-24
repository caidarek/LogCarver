using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class SnapshotBuilderTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0);

    private static IReadOnlyDictionary<string, object?> Row(int id, string note) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note };

    private static RowEvent MakeEvent(
        string lsn, RowEventKind kind, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after,
        DateTime? timestamp, string pageId = "0001:0F", int slotId = 0, string? note = null) =>
        new(lsn, kind, before, after, note, timestamp, pageId, slotId);

    [Fact]
    public void RowInsertedBeforeAsOf_AndNeverTouchedAgain_AppearsInSnapshot()
    {
        var history = new[] { MakeEvent("l1", RowEventKind.Insert, null, Row(1, "a"), T0) };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(5));

        var row = Assert.Single(result.Rows);
        Assert.Equal("a", row.Values!["Note"]);
    }

    [Fact]
    public void AsOfBetweenInsertAndUpdate_ShowsTheInsertedValue_NotTheLaterUpdate()
    {
        var history = new[]
        {
            MakeEvent("l1", RowEventKind.Insert, null, Row(1, "original"), T0),
            MakeEvent("l2", RowEventKind.Update, Row(1, "original"), Row(1, "changed"), T0.AddMinutes(10)),
        };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(5));

        var row = Assert.Single(result.Rows);
        Assert.Equal("original", row.Values!["Note"]);
    }

    [Fact]
    public void AsOfAfterUpdate_ShowsTheUpdatedValue()
    {
        var history = new[]
        {
            MakeEvent("l1", RowEventKind.Insert, null, Row(1, "original"), T0),
            MakeEvent("l2", RowEventKind.Update, Row(1, "original"), Row(1, "changed"), T0.AddMinutes(10)),
        };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(15));

        var row = Assert.Single(result.Rows);
        Assert.Equal("changed", row.Values!["Note"]);
    }

    [Fact]
    public void RowDeletedBeforeAsOf_IsExcluded()
    {
        var history = new[]
        {
            MakeEvent("l1", RowEventKind.Insert, null, Row(1, "a"), T0),
            MakeEvent("l2", RowEventKind.Delete, Row(1, "a"), null, T0.AddMinutes(10)),
        };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(20));

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void RowInsertedAfterAsOf_IsExcluded_DidNotExistYet()
    {
        var history = new[] { MakeEvent("l1", RowEventKind.Insert, null, Row(1, "a"), T0.AddMinutes(10)) };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0);

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void MultipleDistinctRows_AreTrackedIndependentlyByPhysicalSlot()
    {
        var history = new[]
        {
            MakeEvent("l1", RowEventKind.Insert, null, Row(1, "row-a"), T0, slotId: 0),
            MakeEvent("l2", RowEventKind.Insert, null, Row(2, "row-b"), T0, slotId: 1),
            MakeEvent("l3", RowEventKind.Delete, Row(1, "row-a"), null, T0.AddMinutes(5), slotId: 0),
        };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(10));

        var row = Assert.Single(result.Rows);
        Assert.Equal("row-b", row.Values!["Note"]); // slot 0 (row-a) deleted, slot 1 (row-b) remains
    }

    [Fact]
    public void EventWithUnresolvedTimestamp_IsNotUsed_AndIsCounted()
    {
        var history = new[]
        {
            MakeEvent("l1", RowEventKind.Insert, null, Row(1, "a"), T0),
            MakeEvent("l2", RowEventKind.Update, Row(1, "a"), Row(1, "b"), timestamp: null), // can't place in time
        };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(10));

        var row = Assert.Single(result.Rows);
        Assert.Equal("a", row.Values!["Note"]); // the undated update was ignored, not guessed into the result
        Assert.Equal(1, result.EventsWithUnresolvedTimestampIgnored);
    }

    [Fact]
    public void RefusedEventIsTheLatestApplicable_RowIncludedWithNullValuesAndNote_NotSilentlyDropped()
    {
        var history = new[]
        {
            MakeEvent("l1", RowEventKind.Insert, null, null, T0, note: "predates a schema-changing DDL"),
        };

        var result = SnapshotBuilder.BuildSnapshot(history, asOf: T0.AddMinutes(5));

        var row = Assert.Single(result.Rows);
        Assert.Null(row.Values);
        Assert.NotNull(row.Note);
    }
}
