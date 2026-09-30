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
/// entire column layout N times over (once per partition_id). For an
/// ordinary partitioned table these N rows are identical (a column's
/// physical offset/type/nullability doesn't vary by partition, only which
/// physical partition a given ROW lives in does), and returning every one
/// of those duplicates used to corrupt RowDecoder's positional
/// variable-length-column matching downstream (schema.Where(LeafOffset
/// &lt; 0).OrderByDescending(LeafOffset), matched index-for-index against
/// the row's own offset array - N duplicates per column shifts that
/// mapping and decodes wrong values, not an exception).
///
/// GetTableSchemaAsync de-duplicates by ColumnId, but does NOT blindly
/// keep whichever partition's row came back first: SQL Server allows
/// per-partition DATA_COMPRESSION (ALTER TABLE ... REBUILD PARTITION = n
/// WITH (DATA_COMPRESSION = PAGE)), and a compressed partition's rows use
/// a different physical layout than an uncompressed one - a mismatch here
/// would mean silently applying one partition's layout to another
/// partition's differently-shaped rows, an even quieter version of the
/// exact bug this fix closes. Two rows for the same ColumnId that
/// disagree on anything decode-relevant throw rather than pick one
/// arbitrarily. LogCarverOffline's CaptureMetadata already refuses any
/// compressed table outright (a blunter, already-existing gate this one
/// backs up); the public/online LogCarver.Cli currently only warns on
/// compression and continues, so this is this path's only real protection
/// against mixed per-partition compression.
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
        // multiple times over.
        var results = new Dictionary<int, ColumnSchema>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int columnId = reader.GetInt32(reader.GetOrdinal("ColumnId"));
            var candidate = new ColumnSchema(
                Name: reader.GetString(reader.GetOrdinal("ColName")),
                ColumnId: columnId,
                LeafOffset: reader.GetInt16(reader.GetOrdinal("LeafOffset")),
                LeafNullBit: reader.GetInt16(reader.GetOrdinal("LeafNullBit")),
                MaxLength: reader.GetInt16(reader.GetOrdinal("MaxLength")),
                SystemTypeId: reader.GetByte(reader.GetOrdinal("SystemTypeId")),
                Scale: reader.GetByte(reader.GetOrdinal("Scale")));

            if (results.TryGetValue(columnId, out var existing))
            {
                // record equality compares every property - any
                // disagreement means two partitions' physical layouts for
                // this column genuinely differ (see doc comment).
                if (existing != candidate)
                    throw new NotSupportedException(
                        $"Column '{candidate.Name}' has inconsistent physical layout across '{tableName}''s partitions " +
                        "(possibly mixed per-partition DATA_COMPRESSION) - refusing rather than guessing which " +
                        "partition's layout applies to which rows.");
                continue;
            }
            results[columnId] = candidate;
        }
        return results.Values.OrderBy(c => c.ColumnId).ToList();
    }
}
