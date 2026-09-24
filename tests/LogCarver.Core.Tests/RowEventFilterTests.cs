using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class RowEventFilterTests
{
    private static IReadOnlyDictionary<string, object?> Row(int id, string note) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note };

    private static RowEvent MakeEvent(
        RowEventKind kind, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after, string? note = null) =>
        new("lsn1", kind, before, after, note, null, "0001:0F", 1);

    [Fact]
    public void MatchesKey_MatchesOnBeforeImage()
    {
        var evt = MakeEvent(RowEventKind.Update, Row(5, "old"), Row(5, "new"));
        Assert.True(RowEventFilter.MatchesKey(evt, "Note", "old"));
    }

    [Fact]
    public void MatchesKey_MatchesOnAfterImage()
    {
        var evt = MakeEvent(RowEventKind.Update, Row(5, "old"), Row(5, "new"));
        Assert.True(RowEventFilter.MatchesKey(evt, "Note", "new"));
    }

    [Fact]
    public void MatchesKey_IsCaseInsensitiveOnValue()
    {
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "Hello"));
        Assert.True(RowEventFilter.MatchesKey(evt, "Note", "hello"));
    }

    [Fact]
    public void MatchesKey_NoMatchWhenValueDiffers()
    {
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "Hello"));
        Assert.False(RowEventFilter.MatchesKey(evt, "Id", "6"));
    }

    [Fact]
    public void MatchesKey_UnknownColumn_DoesNotMatch()
    {
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "Hello"));
        Assert.False(RowEventFilter.MatchesKey(evt, "NoSuchColumn", "5"));
    }

    [Fact]
    public void MatchesKey_RefusedEvent_NeitherImageAvailable_DoesNotMatch()
    {
        // A schema-drift-refused event has Before=After=null - must not
        // throw, and correctly reports no match rather than a false positive.
        var evt = MakeEvent(RowEventKind.Update, null, null, "refused");
        Assert.False(RowEventFilter.MatchesKey(evt, "Id", "5"));
    }
}
