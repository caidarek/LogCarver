namespace LogCarver.Core.SqlServer;

/// <summary>
/// Shared plumbing for UndoSqlGenerator and ReplaySqlGenerator - the two
/// are mirror images of each other (undo: after -&gt; before, replay:
/// before -&gt; after), so the statement-building and literal-formatting
/// logic lives here once instead of twice.
/// </summary>
internal static class SqlStatementBuilder
{
    public static string BuildInsert(IReadOnlyDictionary<string, object?> values, string tableName)
    {
        string columns = string.Join(", ", values.Keys);
        string literals = string.Join(", ", values.Values.Select(FormatSqlLiteral));
        return $"INSERT INTO {tableName} ({columns}) VALUES ({literals});";
    }

    public static string BuildDelete(IReadOnlyDictionary<string, object?> matchValues, string tableName) =>
        $"DELETE FROM {tableName} WHERE {BuildWhereClause(matchValues)};";

    /// <param name="setValues">The state to write.</param>
    /// <param name="matchValues">
    /// The state to match in WHERE - every column, not just a primary key
    /// (a real table may not have a clean one). This also serves as an
    /// optimistic-concurrency guard: if the row has since changed away
    /// from this state, the WHERE won't match and the statement becomes a
    /// safe no-op instead of silently overwriting it.
    /// </param>
    public static string BuildUpdate(
        IReadOnlyDictionary<string, object?> setValues, IReadOnlyDictionary<string, object?> matchValues, string tableName)
    {
        string setClause = string.Join(", ", setValues.Select(kv => $"{kv.Key} = {FormatSqlLiteral(kv.Value)}"));
        return $"UPDATE {tableName} SET {setClause} WHERE {BuildWhereClause(matchValues)};";
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
