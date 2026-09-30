using System.Text.Json;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class RowEventExporterTests
{
    private static readonly IReadOnlyList<ColumnSchema> Schema =
    [
        new ColumnSchema("Id", 1, 4, 1, 4, 56),
        new ColumnSchema("Note", 2, -1, 2, 200, 167),
    ];

    private static IReadOnlyDictionary<string, object?> Row(int id, string? note) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note };

    private static RowEvent MakeEvent(
        RowEventKind kind, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after, string? note = null) =>
        new("0001:0002:0003", kind, before, after, note, new DateTime(2026, 9, 28, 12, 0, 0, 500), "0001:0F", 1);

    [Fact]
    public void ToCsv_HasOneColumnPerObservedColumn_NotAFlattenedCell()
    {
        var events = new[] { MakeEvent(RowEventKind.Insert, null, Row(5, "hello")) };

        string csv = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Lsn,Timestamp,Kind,Before_Id,Before_Note,After_Id,After_Note,Note", lines[0]);
        Assert.Equal("0001:0002:0003,2026-09-28 12:00:00.500,INSERT,,,5,hello,", lines[1]);
    }

    [Fact]
    public void ToCsv_QuotesAndDoublesUpEmbeddedCommaAndQuote()
    {
        var events = new[] { MakeEvent(RowEventKind.Insert, null, Row(5, "hello, \"world\"")) };

        string csv = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("\"hello, \"\"world\"\"\"", lines[1]);
    }

    [Fact]
    public void ToCsv_NullColumnValue_IsAnEmptyCellNotTheWordNull()
    {
        var events = new[] { MakeEvent(RowEventKind.Insert, null, Row(5, null)) };

        string csv = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.EndsWith(",5,,", lines[1]); // After_Id=5, After_Note=(empty), Note=(empty)
    }

    [Fact]
    public void ToCsv_UndoAndReplayColumns_OnlyAppearWhenRequested()
    {
        var events = new[] { MakeEvent(RowEventKind.Delete, Row(5, "hello"), null) };

        string withoutSql = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        string withSql = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: true, includeReplaySql: true);

        Assert.DoesNotContain("UndoSql", withoutSql);
        Assert.Contains("UndoSql,ReplaySql", withSql);
        Assert.Contains("INSERT INTO [dbo].[Orders]", withSql); // the undo for a DELETE
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(1,1)")]
    public void ToCsv_ValueStartingWithAFormulaTriggerChar_GetsAnApostrophePrefix_ToPreventFormulaInjection(string maliciousValue)
    {
        var events = new[] { MakeEvent(RowEventKind.Insert, null, Row(5, maliciousValue)) };

        string csv = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);

        Assert.Contains($"'{maliciousValue}", csv);
    }

    [Fact]
    public void ToCsv_OrdinaryValueNotStartingWithATriggerChar_IsUnaffected()
    {
        var events = new[] { MakeEvent(RowEventKind.Insert, null, Row(5, "hello")) };

        string csv = RowEventExporter.ToCsv(events, Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);

        Assert.Contains(",5,hello,", csv);
    }

    [Fact]
    public void ToSql_UndoAndReplaySections_OnlyAppearWhenRequested()
    {
        var events = new[] { MakeEvent(RowEventKind.Delete, Row(5, "hello"), null) };

        string undoOnly = RowEventExporter.ToSql(events, "dbo.Orders", includeUndoSql: true, includeReplaySql: false);
        string replayOnly = RowEventExporter.ToSql(events, "dbo.Orders", includeUndoSql: false, includeReplaySql: true);

        Assert.Contains("-- UNDO", undoOnly);
        Assert.Contains("INSERT INTO [dbo].[Orders]", undoOnly); // the undo for a DELETE
        Assert.DoesNotContain("-- REPLAY", undoOnly);

        Assert.Contains("-- REPLAY", replayOnly);
        Assert.Contains("DELETE FROM [dbo].[Orders]", replayOnly); // the replay for a DELETE
        Assert.DoesNotContain("-- UNDO", replayOnly);
    }

    [Fact]
    public void ToSql_WithNeitherUndoNorReplayRequested_SkipsEveryEvent_ReturningAnEmptyScript()
    {
        var events = new[] { MakeEvent(RowEventKind.Delete, Row(5, "hello"), null) };

        string sql = RowEventExporter.ToSql(events, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);

        Assert.Equal("", sql);
    }

    [Fact]
    public void ToSql_HeaderComment_HasLsnTimestampAndUppercaseKind()
    {
        var events = new[] { MakeEvent(RowEventKind.Insert, null, Row(5, "hello")) };

        string sql = RowEventExporter.ToSql(events, "dbo.Orders", includeUndoSql: true, includeReplaySql: false);

        Assert.Contains("-- [0001:0002:0003 2026-09-28 12:00:00.500] INSERT", sql);
    }

    [Fact]
    public void ToSql_WithNoResolvableTimestamp_PrintsTimeUnknownInTheHeaderComment()
    {
        var events = new[]
        {
            new RowEvent("0001:0002:0003", RowEventKind.Insert, null, Row(5, "hello"), null, Timestamp: null, "0001:0F", 1),
        };

        string sql = RowEventExporter.ToSql(events, "dbo.Orders", includeUndoSql: true, includeReplaySql: false);

        Assert.Contains("-- [0001:0002:0003 time unknown] INSERT", sql);
    }

    [Fact]
    public void ToJson_RoundTripsKindAsUppercaseStringAndPreservesColumnValues()
    {
        var events = new[] { MakeEvent(RowEventKind.Update, Row(5, "old"), Row(5, "new")) };

        string json = RowEventExporter.ToJson(events, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];

        Assert.Equal("UPDATE", first.GetProperty("Kind").GetString());
        Assert.Equal("0001:0002:0003", first.GetProperty("Lsn").GetString());
        Assert.Equal(5, first.GetProperty("Before").GetProperty("Id").GetInt32());
        Assert.Equal("new", first.GetProperty("After").GetProperty("Note").GetString());
    }

    [Fact]
    public void ToJson_IncludesUndoSqlOnlyWhenRequested()
    {
        var events = new[] { MakeEvent(RowEventKind.Delete, Row(5, "hello"), null) };

        string json = RowEventExporter.ToJson(events, "dbo.Orders", includeUndoSql: true, includeReplaySql: false);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];

        Assert.Contains("INSERT INTO [dbo].[Orders]", first.GetProperty("UndoSql").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("ReplaySql").ValueKind);
    }

    /// <summary>
    /// Yields once then throws on any further enumeration attempt - proves
    /// a caller genuinely single-pass-streaming a real record source (e.g.
    /// records read incrementally from a multi-GB .ldf) works correctly.
    /// If WriteCsv/WriteSql/WriteJson ever regressed to enumerating twice
    /// (e.g. a `.Count()` added for some future feature), this fails loudly
    /// instead of silently doubling cost or skipping half the data.
    /// </summary>
    private sealed class SinglePassEvents(RowEvent evt) : IEnumerable<RowEvent>
    {
        private bool _consumed;

        public IEnumerator<RowEvent> GetEnumerator()
        {
            if (_consumed) throw new InvalidOperationException("Enumerated more than once - not single-pass-safe.");
            _consumed = true;
            yield return evt;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void WriteCsv_WriteSql_WriteJson_OnlyEnumerateEventsOnce()
    {
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "hello"));

        var csvWriter = new StringWriter();
        RowEventExporter.WriteCsv(csvWriter, new SinglePassEvents(evt), Schema, "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        Assert.Contains(",5,hello,", csvWriter.ToString());

        var sqlWriter = new StringWriter();
        int produced = RowEventExporter.WriteSql(sqlWriter, new SinglePassEvents(evt), "dbo.Orders", includeUndoSql: true, includeReplaySql: false);
        Assert.Equal(1, produced);

        var jsonWriter = new StringWriter();
        RowEventExporter.WriteJson(jsonWriter, new SinglePassEvents(evt), "dbo.Orders", includeUndoSql: false, includeReplaySql: false);
        Assert.Contains("\"Lsn\":\"0001:0002:0003\"", jsonWriter.ToString());
    }
}
