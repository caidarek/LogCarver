using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

public sealed record DdlBoundary(string Lsn, string? TransactionName, string TransactionId);

/// <summary>
/// Finds the LSNs of DDL transactions that changed a table's physical row
/// layout, so records written before them can be refused rather than
/// silently decoded wrong (研究紀錄 第九節 - "最危險的發現").
///
/// Correlation method: LOP_HOBT_DDL fires for both metadata-only DDL (e.g.
/// ADD COLUMN, which touches zero data pages) and physical rewrites (e.g.
/// ALTER COLUMN changing a type) alike, and its [Description] field always
/// contains "rowset &lt;hobt_id&gt;". Matching on hobt_id - not on whether the
/// same transaction also touched the table's AllocUnitName - is what makes
/// this work for metadata-only DDL too; an AllocUnitName-based join finds
/// zero boundaries for ADD COLUMN and silently defeats the guard.
/// </summary>
public static class DdlBoundaryReader
{
    private const string HobtIdSql = "SELECT hobt_id FROM sys.partitions WHERE object_id = OBJECT_ID(@tableName);";

    private const string BoundarySql = """
        SELECT DISTINCT bx.[Current LSN] AS DdlLsn, bx.[Transaction Name] AS TxName, bx.[Transaction ID] AS TxId
        FROM fn_dblog(NULL, NULL) bx
        JOIN (
            SELECT DISTINCT [Transaction ID]
            FROM fn_dblog(NULL, NULL)
            WHERE [Operation] = 'LOP_HOBT_DDL' AND [Description] LIKE @rowsetPattern
        ) ddl ON ddl.[Transaction ID] = bx.[Transaction ID]
        WHERE bx.[Operation] = 'LOP_BEGIN_XACT'
        ORDER BY bx.[Current LSN];
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    public static async Task<IReadOnlyList<DdlBoundary>> GetDdlBoundariesAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        long? hobtId = await GetHobtIdAsync(connection, tableName, ct);
        if (hobtId is null) return [];

        await using var command = new SqlCommand(BoundarySql, connection);
        command.Parameters.AddWithValue("@rowsetPattern", $"%rowset {hobtId.Value}.%");

        var results = new List<DdlBoundary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        int ordLsn = reader.GetOrdinal("DdlLsn");
        int ordName = reader.GetOrdinal("TxName");
        int ordId = reader.GetOrdinal("TxId");
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DdlBoundary(
                Lsn: reader.GetString(ordLsn),
                TransactionName: reader.IsDBNull(ordName) ? null : reader.GetString(ordName),
                TransactionId: reader.GetString(ordId)));
        }
        return results;
    }

    private static async Task<long?> GetHobtIdAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = new SqlCommand(HobtIdSql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (long)result;
    }
}
