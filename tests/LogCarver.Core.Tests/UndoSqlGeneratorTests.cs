using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class UndoSqlGeneratorTests
{
    private static IReadOnlyDictionary<string, object?> Row(int id, string? note, int? amount) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note, ["Amount"] = amount };

    private static RowEvent MakeEvent(
        RowEventKind kind, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after, string? note = null) =>
        new("lsn1", kind, before, after, note, null, "0001:0F", 1);

    [Fact]
    public void Insert_GeneratesDeleteMatchingTheInsertedRow()
    {
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "hello", 100));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [Note] = N'hello' AND [Amount] = 100;", sql);
    }

    [Fact]
    public void Delete_GeneratesInsertOfTheDeletedRow()
    {
        var evt = MakeEvent(RowEventKind.Delete, Row(5, "hello", 100), null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("INSERT INTO [dbo].[Orders] ([Id], [Note], [Amount]) VALUES (5, N'hello', 100);", sql);
    }

    [Fact]
    public void Update_SetsBeforeValues_MatchesOnAfterValues()
    {
        var evt = MakeEvent(RowEventKind.Update, Row(5, "old", 100), Row(5, "new", 200));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal(
            "UPDATE [dbo].[Orders] SET [Id] = 5, [Note] = N'old', [Amount] = 100 WHERE [Id] = 5 AND [Note] = N'new' AND [Amount] = 200;",
            sql);
    }

    [Fact]
    public void NullValues_UseIsNullInWhereAndNullLiteralInSetOrValues()
    {
        var evt = MakeEvent(RowEventKind.Update, Row(5, null, 1), Row(5, "was-null", null));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("[Note] = NULL", sql); // restoring to NULL in SET
        Assert.Contains("[Amount] IS NULL", sql); // matching a NULL current value in WHERE
    }

    [Fact]
    public void StringWithEmbeddedSingleQuote_IsEscapedByDoubling()
    {
        var evt = MakeEvent(RowEventKind.Delete, Row(5, "O'Brien", 1), null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("N'O''Brien'", sql);
    }

    [Fact]
    public void ColumnNameContainingSqlSyntax_IsBracketEscapedNotInjected()
    {
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["Weird]; DROP TABLE X; --"] = "v" };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("[Weird]]; DROP TABLE X; --]", sql);
    }

    [Fact]
    public void RefusedEvent_NoImagesAvailable_ReturnsNullInsteadOfThrowing()
    {
        var evt = MakeEvent(RowEventKind.Update, null, null, "refused");

        Assert.Null(UndoSqlGenerator.Generate(evt, "dbo.Orders"));
    }

    [Fact]
    public void DecimalValue_FormatsAsPlainNumericLiteral_NotThrows()
    {
        // SqlStatementBuilder.FormatSqlLiteral had no case for `decimal` -
        // any row with a DECIMAL/NUMERIC column (added to RowDecoder
        // separately) crashed the entire Undo/Replay generation outright
        // with "Cannot format a SQL literal for value of type
        // System.Decimal" instead of producing a statement for this one
        // row, taking every other row in the export down with it. Real
        // customer impact: DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice columns.
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["Score"] = 123.45m };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [Score] = 123.45;", sql);
    }

    [Fact]
    public void NegativeDecimalValue_FormatsWithoutScientificNotation()
    {
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["Score"] = -123456789012.3456m };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [Score] = -123456789012.3456;", sql);
    }
}
