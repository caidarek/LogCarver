using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// A plain, runnable-looking .sql script: one comment line identifying
    /// the source event (LSN/timestamp/kind) followed by its Undo and/or
    /// Replay statement. Caller must request at least one of the two -
    /// a script with only comment lines isn't a useful export.
    /// </summary>
    public static string ToSql(
        IReadOnlyList<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            string? undoSql = includeUndoSql ? UndoSqlGenerator.Generate(e, tableName) : null;
            string? replaySql = includeReplaySql ? ReplaySqlGenerator.Generate(e, tableName) : null;
            if (undoSql is null && replaySql is null)
                continue;

            string when = e.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "time unknown";
            sb.Append($"-- [{e.Lsn} {when}] {e.Kind.ToString().ToUpperInvariant()}").Append('\n');
            if (undoSql is not null) sb.Append("-- UNDO\n").Append(undoSql).Append('\n');
            if (replaySql is not null) sb.Append("-- REPLAY\n").Append(replaySql).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Built with System.Text.Json.Nodes (JsonObject/JsonArray/JsonValue)
    /// rather than JsonSerializer.Serialize&lt;T&gt; against a POCO -
    /// LogCarverOffline.Cli is PublishAot, which disables the
    /// reflection-based serializer this method used to rely on, and the
    /// row values here are a bag of `object?` with no fixed shape a
    /// source-generated JsonSerializerContext could describe. JsonNode's
    /// own writer is hand-coded, not reflection, so it works under AOT.
    /// </summary>
    public static string ToJson(IReadOnlyList<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql)
    {
        var array = new JsonArray();
        foreach (var e in events)
        {
            var obj = new JsonObject
            {
                ["Lsn"] = e.Lsn,
                ["Timestamp"] = e.Timestamp is { } t ? JsonValue.Create(t) : null,
                ["Kind"] = e.Kind.ToString().ToUpperInvariant(),
                ["Before"] = ToJsonObject(e.Before),
                ["After"] = ToJsonObject(e.After),
                ["Note"] = e.Note,
                ["UndoSql"] = includeUndoSql ? UndoSqlGenerator.Generate(e, tableName) : null,
                ["ReplaySql"] = includeReplaySql ? ReplaySqlGenerator.Generate(e, tableName) : null,
            };
            // Not array.Add(obj): JsonArray.Add<T>(T) is an exact-type
            // generic match, so plain overload resolution picks it over
            // the non-generic Add(JsonNode?) - and that generic overload
            // needs runtime codegen for a non-primitive T, which is exactly
            // what PublishAot's trim/AOT analysis (IL2026/IL3050) flagged
            // building this project. The explicit cast forces the
            // non-generic, reflection-free overload instead.
            array.Add((JsonNode?)obj);
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? ToJsonObject(IReadOnlyDictionary<string, object?>? row)
    {
        if (row is null) return null;
        var obj = new JsonObject();
        foreach (var (key, value) in row)
        {
            obj[key] = value switch
            {
                null => null,
                int i => JsonValue.Create(i),
                DateTime dt => JsonValue.Create(dt),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return obj;
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
}
