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

var ddlBoundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, tableName);
var ddlBoundaryLsns = ddlBoundaries.Select(b => b.Lsn).ToList();
if (ddlBoundaries.Count > 0)
{
    Console.WriteLine($"Detected {ddlBoundaries.Count} schema-changing DDL boundary(ies) for {tableName}:");
    foreach (var b in ddlBoundaries)
        Console.WriteLine($"  LSN {b.Lsn}: {b.TransactionName ?? "(unnamed transaction)"}");
    Console.WriteLine("Records older than the newest boundary will be refused, not guessed.");
    Console.WriteLine();
}

var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, tableName);
var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns);
Console.WriteLine($"{history.Count} row event(s) for {tableName}:");
Console.WriteLine();

foreach (var e in history)
{
    string label = e.Kind switch
    {
        RowEventKind.Insert => "INSERT",
        RowEventKind.Delete => "DELETE",
        RowEventKind.Update => "UPDATE",
        _ => e.Kind.ToString(),
    };

    if (e.Before is null && e.After is null)
    {
        Console.WriteLine($"[{e.Lsn}] {label}: not shown - {e.Note}");
        continue;
    }

    string beforeText = e.Before is null ? "(none)" : Format(e.Before);
    string afterText = e.After is null ? "(none)" : Format(e.After);

    Console.WriteLine(e.Kind == RowEventKind.Update
        ? $"[{e.Lsn}] {label}: {beforeText} -> {afterText}"
        : $"[{e.Lsn}] {label}: {(e.Kind == RowEventKind.Insert ? afterText : beforeText)}");

    if (e.Note is not null)
        Console.WriteLine($"    ({e.Note})");
}

return 0;

static string Format(IReadOnlyDictionary<string, object?> row) =>
    string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
