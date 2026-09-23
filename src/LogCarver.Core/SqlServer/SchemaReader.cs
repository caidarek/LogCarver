using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads a table's physical row layout from sys.system_internals_partition_columns
/// (undocumented, but this is exactly the metadata RowDecoder needs - see
/// CLAUDE.md and 研究紀錄 第八節 for why this beats hardcoding a type map).
/// </summary>
public static class SchemaReader
{
    private const string Sql = """
        SELECT c.name AS ColName, c.column_id AS ColumnId,
               ipc.leaf_offset AS LeafOffset, ipc.leaf_null_bit AS LeafNullBit,
               ipc.max_length AS MaxLength, ipc.system_type_id AS SystemTypeId
        FROM sys.system_internals_partition_columns ipc
        JOIN sys.partitions p ON p.partition_id = ipc.partition_id
        JOIN sys.columns c ON c.object_id = p.object_id AND c.column_id = ipc.partition_column_id
        WHERE p.object_id = OBJECT_ID(@tableName)
        ORDER BY c.column_id;
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    public static async Task<IReadOnlyList<ColumnSchema>> GetTableSchemaAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);

        var results = new List<ColumnSchema>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnSchema(
                Name: reader.GetString(reader.GetOrdinal("ColName")),
                ColumnId: reader.GetInt32(reader.GetOrdinal("ColumnId")),
                LeafOffset: reader.GetInt16(reader.GetOrdinal("LeafOffset")),
                LeafNullBit: reader.GetInt16(reader.GetOrdinal("LeafNullBit")),
                MaxLength: reader.GetInt16(reader.GetOrdinal("MaxLength")),
                SystemTypeId: reader.GetByte(reader.GetOrdinal("SystemTypeId"))));
        }
        return results;
    }
}
