namespace LogCarver.Core.SqlServer;

/// <summary>
/// Filters a table's reconstructed history down to events touching one
/// specific row ("單筆資料歷史", 研究紀錄 功能2), identified by a column/value
/// pair rather than physical location - Page ID/Slot ID is how
/// RowHistoryReconstructor tracks identity internally, but it means
/// nothing to someone asking "what happened to the order with Id=5".
/// </summary>
public static class RowEventFilter
{
    /// <summary>
    /// True if either the before or after image of <paramref name="evt"/>
    /// has <paramref name="column"/> equal to <paramref name="value"/>
    /// (case-insensitive string comparison of the decoded value). An
    /// event whose image is null - refused by the schema-drift guard, or
    /// this side doesn't apply (e.g. an INSERT has no "before") - never
    /// matches on that side, but the other side is still checked.
    /// </summary>
    public static bool MatchesKey(RowEvent evt, string column, string value) =>
        MatchesKey(evt.Before, column, value) || MatchesKey(evt.After, column, value);

    private static bool MatchesKey(IReadOnlyDictionary<string, object?>? row, string column, string value)
    {
        if (row is null || !row.TryGetValue(column, out var actual)) return false;
        return string.Equals(actual?.ToString(), value, StringComparison.OrdinalIgnoreCase);
    }
}
