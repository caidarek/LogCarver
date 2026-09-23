using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;

if (args.Length < 3)
{
    Console.WriteLine("Usage: LogCarver.Cli <server> <database> <schema.table>");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest");
    return 1;
}

string server = args[0];
string database = args[1];
string tableName = args[2];

// TrustServerCertificate=true is a pragmatic default for local/dev SQL Server
// instances with self-signed certs, matching how the research phase worked
// around the same issue (sqlcmd -C). A real deployment should make this configurable.
var connectionString = $"Server={server};Database={database};Integrated Security=true;TrustServerCertificate=true;";

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

var versionCheck = await SqlServerVersion.CheckAsync(connection);
if (!versionCheck.IsValidated)
{
    Console.WriteLine(
        $"WARNING: SQL Server {versionCheck.ProductVersion} has not been validated. " +
        $"Decoded values may be wrong without any error. Validated versions: {string.Join(", ", SqlServerVersion.ValidatedVersions)}");
}

var schema = await SchemaReader.GetTableSchemaAsync(connection, tableName);
if (schema.Count == 0)
{
    Console.WriteLine($"No schema found for '{tableName}'. Does it exist in {database}?");
    return 1;
}

var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, tableName);
Console.WriteLine($"{records.Count} log record(s) for {tableName}:");
Console.WriteLine();

foreach (var record in records)
{
    switch (record.Operation)
    {
        case "LOP_INSERT_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
            PrintRow(record.Lsn, "INSERT", bytes, schema);
            break;

        case "LOP_DELETE_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
            PrintRow(record.Lsn, "DELETE", bytes, schema);
            break;

        case "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS":
            // Reconstructing before/after here needs the splice algorithm
            // (研究紀錄 第八節) against a known prior row image - not wired
            // up yet. Report that an update happened without guessing at values.
            Console.WriteLine($"[{record.Lsn}] UPDATE at offset {record.OffsetInRow} (before/after reconstruction not implemented yet)");
            break;
    }
}

return 0;

static void PrintRow(string lsn, string label, byte[] rowBytes, IReadOnlyList<ColumnSchema> schema)
{
    try
    {
        var decoded = RowDecoder.Decode(rowBytes, schema);
        var fields = string.Join(", ", decoded.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
        Console.WriteLine($"[{lsn}] {label}: {fields}");
    }
    catch (NotSupportedException ex)
    {
        Console.WriteLine($"[{lsn}] {label}: could not decode - {ex.Message}");
    }
}
