using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class ReplaySqlGeneratorTests
{
    private static IReadOnlyDictionary<string, object?> Row(int id, string? note, int? amount) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note, ["Amount"] = amount };

    [Fact]
    public void Insert_GeneratesInsertOfTheAfterImage()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, Row(5, "hello", 100), null, null);

        var sql = ReplaySqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("INSERT INTO dbo.Orders (Id, Note, Amount) VALUES (5, N'hello', 100);", sql);
    }

    [Fact]
    public void Delete_GeneratesDeleteMatchingTheBeforeImage()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Delete, Row(5, "hello", 100), null, null, null);

        var sql = ReplaySqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM dbo.Orders WHERE Id = 5 AND Note = N'hello' AND Amount = 100;", sql);
    }

    [Fact]
    public void Update_SetsAfterValues_MatchesOnBeforeValues()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, Row(5, "old", 100), Row(5, "new", 200), null, null);

        var sql = ReplaySqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal(
            "UPDATE dbo.Orders SET Id = 5, Note = N'new', Amount = 200 WHERE Id = 5 AND Note = N'old' AND Amount = 100;",
            sql);
    }

    [Fact]
    public void RefusedEvent_NoImagesAvailable_ReturnsNullInsteadOfThrowing()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, null, null, "refused", null);

        Assert.Null(ReplaySqlGenerator.Generate(evt, "dbo.Orders"));
    }

    [Fact]
    public void UndoThenReplay_OnTheSameEvent_AreExactOpposites()
    {
        // Locks in the mirror-image relationship the two generators are
        // documented to have: replaying forward then undoing should be a
        // no-op sequence of statements over the same before/after pair.
        var evt = new RowEvent("lsn1", RowEventKind.Update, Row(5, "old", 100), Row(5, "new", 200), null, null);

        var undo = UndoSqlGenerator.Generate(evt, "dbo.Orders");
        var replay = ReplaySqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("UPDATE dbo.Orders SET Id = 5, Note = N'old', Amount = 100 WHERE Id = 5 AND Note = N'new' AND Amount = 200;", undo);
        Assert.Equal("UPDATE dbo.Orders SET Id = 5, Note = N'new', Amount = 200 WHERE Id = 5 AND Note = N'old' AND Amount = 100;", replay);
    }
}
