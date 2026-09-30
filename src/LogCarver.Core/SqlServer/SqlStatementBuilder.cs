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
        string columns = string.Join(", ", values.Keys.Select(EscapeIdentifier));
        string literals = string.Join(", ", values.Values.Select(FormatSqlLiteral));
        return $"INSERT INTO {EscapeIdentifier(tableName)} ({columns}) VALUES ({literals});";
    }

    public static string BuildDelete(IReadOnlyDictionary<string, object?> matchValues, string tableName) =>
        $"DELETE FROM {EscapeIdentifier(tableName)} WHERE {BuildWhereClause(matchValues)};";

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
        string setClause = string.Join(", ", setValues.Select(kv => $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}"));
        return $"UPDATE {EscapeIdentifier(tableName)} SET {setClause} WHERE {BuildWhereClause(matchValues)};";
    }

    private static string BuildWhereClause(IReadOnlyDictionary<string, object?> row) =>
        string.Join(" AND ", row.Select(kv => kv.Value is null
            ? $"{EscapeIdentifier(kv.Key)} IS NULL"
            : $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}"));

    // Bracket-quotes each dot-separated part of an identifier (schema.table
    // or a bare column name) and doubles up any embedded "]" - the SQL
    // Server quoted-identifier escaping rule. Column names come from
    // sys.columns metadata rather than a free-text source, but nothing
    // stops a table from having one that contains SQL syntax characters,
    // and this text is meant to be reviewed and run by a human rather than
    // executed by LogCarver itself - an unescaped identifier would let such
    // a column name inject extra statements into what looks like a plain
    // suggestion.
    private static string EscapeIdentifier(string name) =>
        string.Join(".", name.Split('.').Select(part => $"[{part.Replace("]", "]]")}]"));

    private static string FormatSqlLiteral(object? value) => value switch
    {
        null => "NULL",
        int i => i.ToString(),
        // Added alongside BIGINT column decoding in RowDecoder - missed
        // here would repeat exactly the DECIMAL/NUMERIC gap above: a real
        // customer table ([LOG].[JobRun]'s bigint JobID) hitting the
        // NotSupportedException below outright crashes the whole
        // --undo/--replay/--export sql run, losing every other row too.
        long l => l.ToString(),
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        // decimal.ToString() never uses scientific notation (unlike
        // double/float), so this always produces a plain SQL Server
        // decimal/numeric literal - InvariantCulture avoids a comma
        // decimal separator on a non-US locale. Added alongside DECIMAL/
        // NUMERIC column decoding in RowDecoder; missed here until a real
        // customer table (DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice) crashed the whole
        // --undo/--replay/--export sql run outright with "Cannot format a
        // SQL literal for value of type System.Decimal" - every other
        // column in every other row was lost too, not just this value.
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => $"N'{s.Replace("'", "''")}'",
        _ => throw new NotSupportedException($"Cannot format a SQL literal for value of type {value.GetType()}."),
    };
}
