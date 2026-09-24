using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class UndoSqlGeneratorTests
{
    private static IReadOnlyDictionary<string, object?> Row(int id, string? note, int? amount) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note, ["Amount"] = amount };

    [Fact]
    public void Insert_GeneratesDeleteMatchingTheInsertedRow()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, Row(5, "hello", 100), null, null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM dbo.Orders WHERE Id = 5 AND Note = N'hello' AND Amount = 100;", sql);
    }

    [Fact]
    public void Delete_GeneratesInsertOfTheDeletedRow()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Delete, Row(5, "hello", 100), null, null, null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("INSERT INTO dbo.Orders (Id, Note, Amount) VALUES (5, N'hello', 100);", sql);
    }

    [Fact]
    public void Update_SetsBeforeValues_MatchesOnAfterValues()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, Row(5, "old", 100), Row(5, "new", 200), null, null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal(
            "UPDATE dbo.Orders SET Id = 5, Note = N'old', Amount = 100 WHERE Id = 5 AND Note = N'new' AND Amount = 200;",
            sql);
    }

    [Fact]
    public void NullValues_UseIsNullInWhereAndNullLiteralInSetOrValues()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, Row(5, null, 1), Row(5, "was-null", null), null, null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("Note = NULL", sql); // restoring to NULL in SET
        Assert.Contains("Amount IS NULL", sql); // matching a NULL current value in WHERE
    }

    [Fact]
    public void StringWithEmbeddedSingleQuote_IsEscapedByDoubling()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Delete, Row(5, "O'Brien", 1), null, null, null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("N'O''Brien'", sql);
    }

    [Fact]
    public void RefusedEvent_NoImagesAvailable_ReturnsNullInsteadOfThrowing()
    {
        var evt = new RowEvent("lsn1", RowEventKind.Update, null, null, "refused", null);

        Assert.Null(UndoSqlGenerator.Generate(evt, "dbo.Orders"));
    }
}
