using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Serializes reconstructed row events to CSV or JSON for handing off to
/// audit/ticketing systems - the console output is for a human reading it
/// live, not for piping elsewhere.
/// </summary>
public static class RowEventExporter
{
    /// <summary>
    /// One column per observed table column, prefixed Before_/After_ (not
    /// a single flattened "Col=Value, Col2=Value2" cell) so the result is
    /// directly usable in a spreadsheet - pivoting or filtering by one
    /// column's value doesn't work if every column is jammed into one cell.
    /// </summary>
    public static string ToCsv(
        IReadOnlyList<RowEvent> events, IReadOnlyList<ColumnSchema> schema, string tableName,
        bool includeUndoSql, bool includeReplaySql)
    {
        var columns = schema.Select(c => c.Name).ToList();

        var header = new List<string> { "Lsn", "Timestamp", "Kind" };
        header.AddRange(columns.Select(c => $"Before_{c}"));
        header.AddRange(columns.Select(c => $"After_{c}"));
        header.Add("Note");
        if (includeUndoSql) header.Add("UndoSql");
        if (includeReplaySql) header.Add("ReplaySql");

        var sb = new StringBuilder();
        sb.Append(string.Join(",", header.Select(CsvEscape))).Append("\r\n");

        foreach (var e in events)
        {
            var row = new List<string>
            {
                e.Lsn,
                e.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "",
                e.Kind.ToString().ToUpperInvariant(),
            };
            row.AddRange(columns.Select(c => CellValue(e.Before, c)));
            row.AddRange(columns.Select(c => CellValue(e.After, c)));
            row.Add(e.Note ?? "");
            if (includeUndoSql) row.Add(UndoSqlGenerator.Generate(e, tableName) ?? "");
            if (includeReplaySql) row.Add(ReplaySqlGenerator.Generate(e, tableName) ?? "");
            sb.Append(string.Join(",", row.Select(CsvEscape))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql)
    {
        var exportable = events.Select(e => new ExportedEvent(
            e.Lsn,
            e.Timestamp,
            e.Kind.ToString().ToUpperInvariant(),
            e.Before,
            e.After,
            e.Note,
            includeUndoSql ? UndoSqlGenerator.Generate(e, tableName) : null,
            includeReplaySql ? ReplaySqlGenerator.Generate(e, tableName) : null))
            .ToList();
        return JsonSerializer.Serialize(exportable, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string CellValue(IReadOnlyDictionary<string, object?>? row, string column)
    {
        if (row is null || !row.TryGetValue(column, out var value) || value is null)
            return "";
        return value is DateTime dt
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : value.ToString() ?? "";
    }

    // RFC 4180: quote a field only if it contains a comma, quote, or line
    // break, and double up any internal quotes - quoting everything
    // unconditionally would still be correct but is noisier to read.
    private static string CsvEscape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record ExportedEvent(
        string Lsn,
        DateTime? Timestamp,
        string Kind,
        IReadOnlyDictionary<string, object?>? Before,
        IReadOnlyDictionary<string, object?>? After,
        string? Note,
        string? UndoSql,
        string? ReplaySql);
}
