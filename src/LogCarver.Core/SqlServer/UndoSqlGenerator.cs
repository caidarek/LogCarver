namespace LogCarver.Core.SqlServer;

/// <summary>
/// Generates suggested SQL to reverse one row event (研究紀錄 功能3: Undo SQL
/// 產生器). This is output for a human to review before running - LogCarver
/// never executes anything itself.
///
/// The WHERE clause always matches every column of the row's most
/// recently observed state, not just a primary key (a real customer table
/// may not have a clean one). This doubles as an optimistic-concurrency
/// guard: if the row changed after LogCarver observed it, the WHERE won't
/// match and the statement becomes a safe no-op instead of silently
/// overwriting whatever changed it since.
/// </summary>
public static class UndoSqlGenerator
{
    /// <returns>The undo statement, or null if the event has no usable before/after image to work from (e.g. refused by the schema-drift guard).</returns>
    public static string? Generate(RowEvent evt, string tableName) => evt.Kind switch
    {
        RowEventKind.Insert when evt.After is not null => GenerateDelete(evt.After, tableName),
        RowEventKind.Delete when evt.Before is not null => GenerateInsert(evt.Before, tableName),
        RowEventKind.Update when evt.Before is not null && evt.After is not null => GenerateUpdate(evt.Before, evt.After, tableName),
        _ => null,
    };

    private static string GenerateDelete(IReadOnlyDictionary<string, object?> after, string tableName) =>
        $"DELETE FROM {tableName} WHERE {BuildWhereClause(after)};";

    private static string GenerateInsert(IReadOnlyDictionary<string, object?> before, string tableName)
    {
        string columns = string.Join(", ", before.Keys);
        string values = string.Join(", ", before.Values.Select(FormatSqlLiteral));
        return $"INSERT INTO {tableName} ({columns}) VALUES ({values});";
    }

    private static string GenerateUpdate(IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after, string tableName)
    {
        string setClause = string.Join(", ", before.Select(kv => $"{kv.Key} = {FormatSqlLiteral(kv.Value)}"));
        return $"UPDATE {tableName} SET {setClause} WHERE {BuildWhereClause(after)};";
    }

    private static string BuildWhereClause(IReadOnlyDictionary<string, object?> row) =>
        string.Join(" AND ", row.Select(kv => kv.Value is null
            ? $"{kv.Key} IS NULL"
            : $"{kv.Key} = {FormatSqlLiteral(kv.Value)}"));

    private static string FormatSqlLiteral(object? value) => value switch
    {
        null => "NULL",
        int i => i.ToString(),
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        string s => $"N'{s.Replace("'", "''")}'",
        _ => throw new NotSupportedException($"Cannot format a SQL literal for value of type {value.GetType()}."),
    };
}
