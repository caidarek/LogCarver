using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class RowEventFilterTests
{
    private static IReadOnlyDictionary<string, object?> Row(int id, string note) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note };

    [Fact]
    public void MatchesKey_MatchesOnBeforeImage()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, Row(5, "old"), Row(5, "new"), null, null);
        Assert.True(RowEventFilter.MatchesKey(evt, "Note", "old"));
    }

    [Fact]
    public void MatchesKey_MatchesOnAfterImage()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, Row(5, "old"), Row(5, "new"), null, null);
        Assert.True(RowEventFilter.MatchesKey(evt, "Note", "new"));
    }

    [Fact]
    public void MatchesKey_IsCaseInsensitiveOnValue()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, Row(5, "Hello"), null, null);
        Assert.True(RowEventFilter.MatchesKey(evt, "Note", "hello"));
    }

    [Fact]
    public void MatchesKey_NoMatchWhenValueDiffers()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, Row(5, "Hello"), null, null);
        Assert.False(RowEventFilter.MatchesKey(evt, "Id", "6"));
    }

    [Fact]
    public void MatchesKey_UnknownColumn_DoesNotMatch()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, Row(5, "Hello"), null, null);
        Assert.False(RowEventFilter.MatchesKey(evt, "NoSuchColumn", "5"));
    }

    [Fact]
    public void MatchesKey_RefusedEvent_NeitherImageAvailable_DoesNotMatch()
    {
        // A schema-drift-refused event has Before=After=null - must not
        // throw, and correctly reports no match rather than a false positive.
        var evt = new RowEvent("lsn1", RowEventKind.Update, null, null, "refused", null);
        Assert.False(RowEventFilter.MatchesKey(evt, "Id", "5"));
    }
}
