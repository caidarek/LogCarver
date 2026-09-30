using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads a table's physical row layout from sys.system_internals_partition_columns
/// (undocumented, but this is exactly the metadata RowDecoder needs - see
/// CLAUDE.md and 研究紀錄 第八節 for why this beats hardcoding a type map).
///
/// index_id filter: a table can have more than one row in sys.partitions -
/// index_id=0/1 for its own heap/clustered-index storage, plus one more
/// per nonclustered index. Without filtering to 0/1, a heap table with any
/// nonclustered index (e.g. one backing a NONCLUSTERED PRIMARY KEY) joins
/// in that index's own internal column layout alongside the heap's real
/// one, producing bogus SystemTypeId values for columns not covered by the
/// index - found for real on 2026-09-28 via a heap table with a
/// PRIMARY KEY NONCLUSTERED constraint (see SchemaReaderTests).
///
/// Table partitioning: sys.partitions also has one row per PARTITION for a
/// given index_id, not just one - a table with N partitions returns this
/// entire column layout N times over (once per partition_id, all
/// identical - a column's physical offset/type/nullability never varies
/// by partition, only which physical partition a given ROW lives in
/// does). Returning every one of those duplicate rows used to corrupt
/// RowDecoder's positional variable-length-column matching downstream
/// (schema.Where(LeafOffset &lt; 0).OrderByDescending(LeafOffset), matched
/// index-for-index against the row's own offset array - N duplicates per
/// column shifts that mapping and decodes wrong values, not an
/// exception). GetTableSchemaAsync de-duplicates by ColumnId below so a
/// partitioned table's schema still resolves correctly; the separate,
/// harder problem of a partitioned table's actual ROW DATA living across
/// multiple PartitionIds is handled where the row-scanning code chooses
/// which PartitionId(s) to look for (LogCarverOffline's CaptureMetadata),
/// not here.
/// </summary>
public static class SchemaReader
{
    private const string Sql = """
        SELECT c.name AS ColName, c.column_id AS ColumnId,
               ipc.leaf_offset AS LeafOffset, ipc.leaf_null_bit AS LeafNullBit,
               ipc.max_length AS MaxLength, ipc.system_type_id AS SystemTypeId,
               c.scale AS Scale
        FROM sys.system_internals_partition_columns ipc
        JOIN sys.partitions p ON p.partition_id = ipc.partition_id
        JOIN sys.columns c ON c.object_id = p.object_id AND c.column_id = ipc.partition_column_id
        WHERE p.object_id = OBJECT_ID(@tableName) AND p.index_id IN (0, 1)
        ORDER BY c.column_id;
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    public static async Task<IReadOnlyList<ColumnSchema>> GetTableSchemaAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);

        // Keyed by ColumnId, not just appended - see this class's doc
        // comment for why a partitioned table returns the same column
        // multiple times over, and why keeping only the first occurrence
        // (rather than every row) is correct rather than a workaround.
        var results = new Dictionary<int, ColumnSchema>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int columnId = reader.GetInt32(reader.GetOrdinal("ColumnId"));
            if (results.ContainsKey(columnId)) continue;
            results[columnId] = new ColumnSchema(
                Name: reader.GetString(reader.GetOrdinal("ColName")),
                ColumnId: columnId,
                LeafOffset: reader.GetInt16(reader.GetOrdinal("LeafOffset")),
                LeafNullBit: reader.GetInt16(reader.GetOrdinal("LeafNullBit")),
                MaxLength: reader.GetInt16(reader.GetOrdinal("MaxLength")),
                SystemTypeId: reader.GetByte(reader.GetOrdinal("SystemTypeId")),
                Scale: reader.GetByte(reader.GetOrdinal("Scale")));
        }
        return results.Values.OrderBy(c => c.ColumnId).ToList();
    }
}
