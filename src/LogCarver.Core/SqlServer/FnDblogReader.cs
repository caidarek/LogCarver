using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads row-level log records from fn_dblog for one table.
///
/// Scope note: only reads the live/active portion of the log that
/// fn_dblog exposes. Recovering data from VLFs that are reusable but not
/// yet overwritten requires reading the LDF file directly and is not
/// implemented here.
/// </summary>
public static class FnDblogReader
{
    private const string Sql = """
        SELECT [Current LSN] AS Lsn, [Operation] AS Operation, [Context] AS Context,
               [Offset in Row] AS OffsetInRow, [AllocUnitName] AS AllocUnitName,
               [RowLog Contents 0] AS Rlc0, [RowLog Contents 1] AS Rlc1
        FROM fn_dblog(NULL, NULL)
        WHERE [AllocUnitName] LIKE @allocPattern AND [Context] = 'LCX_CLUSTERED'
        ORDER BY [Current LSN];
        """;

    /// <param name="tableName">
    /// Schema-qualified, e.g. "dbo.LogTest". Matched as a LIKE prefix
    /// against AllocUnitName with a literal trailing dot
    /// ("dbo.LogTest.%") - a bare "%tableName%" also matches
    /// differently-named tables that merely share a prefix (e.g.
    /// LogTestAlterType matching a search for LogTest); see 研究紀錄 第九節.
    /// </param>
    public static async Task<IReadOnlyList<LogRecord>> ReadClusteredRecordsAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@allocPattern", $"{tableName}.%");

        var results = new List<LogRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        int ordLsn = reader.GetOrdinal("Lsn");
        int ordOp = reader.GetOrdinal("Operation");
        int ordCtx = reader.GetOrdinal("Context");
        int ordOffset = reader.GetOrdinal("OffsetInRow");
        int ordAlloc = reader.GetOrdinal("AllocUnitName");
        int ordRlc0 = reader.GetOrdinal("Rlc0");
        int ordRlc1 = reader.GetOrdinal("Rlc1");

        while (await reader.ReadAsync(ct))
        {
            results.Add(new LogRecord(
                Lsn: reader.GetString(ordLsn),
                Operation: reader.GetString(ordOp),
                Context: reader.GetString(ordCtx),
                OffsetInRow: reader.IsDBNull(ordOffset) ? null : reader.GetInt16(ordOffset),
                AllocUnitName: reader.IsDBNull(ordAlloc) ? null : reader.GetString(ordAlloc),
                RowLogContents0: reader.IsDBNull(ordRlc0) ? null : (byte[])reader[ordRlc0],
                RowLogContents1: reader.IsDBNull(ordRlc1) ? null : (byte[])reader[ordRlc1]));
        }
        return results;
    }
}
