using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;

if (args.Length < 3)
{
    Console.WriteLine("Usage: LogCarver.Cli <server> <database> <schema.table> [<from>] [<to>]");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest \"2026-09-23T09:00\" \"2026-09-23T10:00\"");
    Console.WriteLine("<from>/<to> filter which events are PRINTED to that incident window;");
    Console.WriteLine("reconstruction still uses the table's full observed history so before/after stay accurate.");
    return 1;
}

string server = args[0];
string database = args[1];
string tableName = args[2];
DateTime? from = args.Length > 3 ? DateTime.Parse(args[3]) : null;
DateTime? to = args.Length > 4 ? DateTime.Parse(args[4]) : null;

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

var compressionTypes = await CompressionChecker.GetCompressionTypesAsync(connection, tableName);
if (CompressionChecker.IsCompressed(compressionTypes))
{
    Console.WriteLine(
        $"'{tableName}' uses {string.Join("/", compressionTypes.Where(t => t != "NONE"))} compression. " +
        "Compressed rows use a different physical layout that this tool does not decode yet - refusing rather than guessing.");
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

var transactionTimes = await TransactionTimeReader.GetTransactionBeginTimesAsync(connection);
var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, tableName);

// Reconstruction always runs over the table's FULL observed history, not
// just the [from,to] window - an UPDATE inside the window still needs its
// "before" image, which may have been written outside it. The time
// window only filters what gets printed below.
var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns, transactionTimes);

var toShow = history.Where(e =>
    (from is null || e.Timestamp is null || e.Timestamp >= from) &&
    (to is null || e.Timestamp is null || e.Timestamp <= to))
    .ToList();

if (from is not null || to is not null)
    Console.WriteLine($"{toShow.Count} of {history.Count} row event(s) for {tableName} fall in [{from}, {to}] (events with no resolvable timestamp are always shown):");
else
    Console.WriteLine($"{toShow.Count} row event(s) for {tableName}:");
Console.WriteLine();

foreach (var e in toShow)
{
    string label = e.Kind switch
    {
        RowEventKind.Insert => "INSERT",
        RowEventKind.Delete => "DELETE",
        RowEventKind.Update => "UPDATE",
        _ => e.Kind.ToString(),
    };
    string when = e.Timestamp is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss.fff") : "time unknown";

    if (e.Before is null && e.After is null)
    {
        Console.WriteLine($"[{e.Lsn} {when}] {label}: not shown - {e.Note}");
        continue;
    }

    string beforeText = e.Before is null ? "(none)" : Format(e.Before);
    string afterText = e.After is null ? "(none)" : Format(e.After);

    Console.WriteLine(e.Kind == RowEventKind.Update
        ? $"[{e.Lsn} {when}] {label}: {beforeText} -> {afterText}"
        : $"[{e.Lsn} {when}] {label}: {(e.Kind == RowEventKind.Insert ? afterText : beforeText)}");

    if (e.Note is not null)
        Console.WriteLine($"    ({e.Note})");
}

return 0;

static string Format(IReadOnlyDictionary<string, object?> row) =>
    string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
