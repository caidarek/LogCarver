using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Compressed rows (ROW or PAGE compression) use a completely different
/// physical layout - a different TagA value and variable-length integer
/// encoding for what would otherwise be fixed-length columns (研究紀錄
/// 壓縮資料表小節) - that RowDecoder does not implement. Unlike LOB, this is
/// a per-table (per-partition) property known in advance from metadata,
/// so it can and should be checked before attempting to decode anything,
/// rather than discovered via a thrown exception mid-row.
/// </summary>
public static class CompressionChecker
{
    private const string Sql = "SELECT DISTINCT data_compression_desc FROM sys.partitions WHERE object_id = OBJECT_ID(@tableName);";

    /// <returns>Distinct compression types across the table's partitions. Empty if the table doesn't exist.</returns>
    public static async Task<IReadOnlyList<string>> GetCompressionTypesAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        var results = new List<string>();
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetString(0));
        return results;
    }

    public static bool IsCompressed(IReadOnlyList<string> compressionTypes) =>
        compressionTypes.Any(t => t != "NONE");
}
