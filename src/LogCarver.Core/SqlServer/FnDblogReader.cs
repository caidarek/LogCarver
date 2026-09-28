using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads row-level log records from fn_dblog for one table.
///
/// AllocUnitName resolution: fn_dblog's AllocUnitName for a table's own row
/// storage is "schema.table" for a heap (index_id=0, whose sys.indexes.name
/// is NULL) or "schema.table.indexname" for a clustered index (index_id=1).
/// This used to be *guessed* from the table name alone via a LIKE prefix
/// ("dbo.LogTest.%") - which works for a clustered table with no other
/// indexes, but two real bugs came from that guess, both found via CLI
/// testing against SQL Server 2019 on 2026-09-28:
///   1. A heap table's AllocUnitName has no index name to append, so it
///      never matched the "table.%" prefix pattern at all - every heap
///      table silently returned zero records.
///   2. A table (heap or clustered) with any OTHER index also has an
///      AllocUnitName of "schema.table.otherindexname" for that index's
///      own maintenance activity - which the loose "table.%" prefix (or a
///      naive "table" exact-match for heaps) also matches, pulling in that
///      index's own internal row layout (just the indexed columns, not the
///      full row) alongside the table's real data.
/// Resolving the table's actual index_id IN (0,1) name via sys.indexes
/// first and matching fn_dblog by exact equality against that one
/// constructed string eliminates both: no pattern, nothing to guess wrong.
///
/// Context filter: a clustered-index table's INSERT/UPDATE use
/// Context='LCX_CLUSTERED', but DELETE uses 'LCX_MARK_AS_GHOST' (SQL
/// Server marks deleted rows as ghosts in-place before background cleanup
/// expunges them) - both must be included or every DELETE silently
/// disappears from the results. A heap table uses a third context,
/// 'LCX_HEAP', for INSERT/UPDATE/DELETE alike.
///
/// Scope note: only reads the live/active portion of the log that
/// fn_dblog exposes. Recovering data from VLFs that are reusable but not
/// yet overwritten requires reading the LDF file directly and is not
/// implemented here.
/// </summary>
public static class FnDblogReader
{
    private const string OwnIndexNameSql = """
        SELECT i.name
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(@tableName) AND i.index_id IN (0, 1);
        """;

    private const string Sql = """
        SELECT [Current LSN] AS Lsn, [Operation] AS Operation, [Context] AS Context,
               [Offset in Row] AS OffsetInRow, [AllocUnitName] AS AllocUnitName,
               [Page ID] AS PageId, [Slot ID] AS SlotId, [Transaction ID] AS TransactionId,
               [RowLog Contents 0] AS Rlc0, [RowLog Contents 1] AS Rlc1
        FROM fn_dblog(NULL, NULL)
        WHERE [AllocUnitName] = @allocUnitName
          AND [Context] IN ('LCX_CLUSTERED', 'LCX_MARK_AS_GHOST', 'LCX_HEAP')
        ORDER BY [Current LSN];
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    public static async Task<IReadOnlyList<LogRecord>> ReadClusteredRecordsAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        string? allocUnitName = await ResolveOwnAllocUnitNameAsync(connection, tableName, ct);
        if (allocUnitName is null)
            return [];

        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@allocUnitName", allocUnitName);

        var results = new List<LogRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        int ordLsn = reader.GetOrdinal("Lsn");
        int ordOp = reader.GetOrdinal("Operation");
        int ordCtx = reader.GetOrdinal("Context");
        int ordOffset = reader.GetOrdinal("OffsetInRow");
        int ordAlloc = reader.GetOrdinal("AllocUnitName");
        int ordPage = reader.GetOrdinal("PageId");
        int ordSlot = reader.GetOrdinal("SlotId");
        int ordTxId = reader.GetOrdinal("TransactionId");
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
                PageId: reader.IsDBNull(ordPage) ? null : reader.GetString(ordPage),
                SlotId: reader.IsDBNull(ordSlot) ? null : reader.GetInt32(ordSlot),
                TransactionId: reader.IsDBNull(ordTxId) ? null : reader.GetString(ordTxId),
                RowLogContents0: reader.IsDBNull(ordRlc0) ? null : (byte[])reader[ordRlc0],
                RowLogContents1: reader.IsDBNull(ordRlc1) ? null : (byte[])reader[ordRlc1]));
        }
        return results;
    }

    /// <summary>
    /// Builds the exact AllocUnitName fn_dblog uses for this table's own
    /// row storage (not any secondary index's), or null if the table
    /// doesn't exist. A heap's sys.indexes.name is NULL for index_id=0.
    /// </summary>
    private static async Task<string?> ResolveOwnAllocUnitNameAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = new SqlCommand(OwnIndexNameSql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null)
            return null;
        return result is DBNull ? tableName : $"{tableName}.{(string)result}";
    }
}
