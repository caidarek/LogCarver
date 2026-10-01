using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

public sealed record TouchedTable(string Schema, string Table, string AllocUnitName);

/// <summary>
/// Given a single Transaction ID, finds every table that transaction touched -
/// needed because a transaction is not guaranteed to stay inside one table (a
/// FK cascade delete, or a trigger, can touch others), and LogCarver Guard's
/// per-transaction undo has to cover all of them, not just whichever one the
/// caller happened to ask about.
///
/// AllocUnitName format (see FnDblogReader's own doc comment for the full
/// derivation): "schema.table" for a heap's own row storage, or
/// "schema.table.indexname" for a clustered index's. Either way, the first
/// two dot-separated segments are the schema and table name - splitting on
/// '.' and taking the first two parts works for both shapes without needing
/// to know ahead of time which one a given AllocUnitName is.
/// </summary>
public static class TransactionScopeReader
{
    private const string Sql = """
        SELECT DISTINCT [AllocUnitName]
        FROM fn_dblog(NULL, NULL)
        WHERE [Transaction ID] = @transactionId
          AND [Operation] IN ('LOP_INSERT_ROWS', 'LOP_DELETE_ROWS', 'LOP_MODIFY_ROW', 'LOP_MODIFY_COLUMNS')
          AND [AllocUnitName] IS NOT NULL;
        """;

    public static async Task<IReadOnlyList<TouchedTable>> GetTouchedTablesAsync(
        SqlConnection connection, string transactionId, CancellationToken ct = default)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@transactionId", transactionId);

        var results = new List<TouchedTable>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string allocUnitName = reader.GetString(0);
            if (TryParseSchemaTable(allocUnitName, out string schema, out string table))
                results.Add(new TouchedTable(schema, table, allocUnitName));
        }
        return results;
    }

    private static bool TryParseSchemaTable(string allocUnitName, out string schema, out string table)
    {
        string[] parts = allocUnitName.Split('.');
        if (parts.Length >= 2)
        {
            schema = parts[0];
            table = parts[1];
            return true;
        }
        schema = "";
        table = "";
        return false;
    }
}
