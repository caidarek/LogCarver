using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;

if (args.Length == 0 || args[0] is "-h" or "--help" or "-?")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

if (args.Length < 3)
{
    Console.WriteLine("Missing required arguments.");
    Console.WriteLine();
    PrintUsage();
    return 1;
}

string server = args[0];
string database = args[1];
string tableName = args[2];

if (!TryParseOptions(args, out var options, out var parseError))
{
    Console.WriteLine(parseError);
    Console.WriteLine();
    PrintUsage();
    return 1;
}

try
{
    return await RunAsync(server, database, tableName, options);
}
catch (SqlException ex)
{
    Console.WriteLine($"Could not connect to or query SQL Server '{server}': {ex.Message}");
    Console.WriteLine("Check that the server name is correct, the instance is running and reachable, and your Windows account has access to the database.");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
    return 1;
}

static bool TryParseOptions(string[] args, out CliOptions options, out string error)
{
    options = new CliOptions();
    error = "";

    int i = 3;
    while (i < args.Length)
    {
        string flag = args[i];

        if (flag == "--undo")
        {
            options.ShowUndoSql = true;
            i += 1;
            continue;
        }

        if (i + 1 >= args.Length)
        {
            error = $"Missing value for '{flag}'.";
            return false;
        }
        string value = args[i + 1];
        i += 2;

        switch (flag)
        {
            case "--from":
                if (!DateTime.TryParse(value, out var from))
                {
                    error = $"Could not parse --from value '{value}' as a date/time. Try a format like \"2026-09-23T09:00\".";
                    return false;
                }
                options.From = from;
                break;

            case "--to":
                if (!DateTime.TryParse(value, out var to))
                {
                    error = $"Could not parse --to value '{value}' as a date/time. Try a format like \"2026-09-23T09:00\".";
                    return false;
                }
                options.To = to;
                break;

            case "--key":
                int eq = value.IndexOf('=');
                if (eq <= 0)
                {
                    error = $"Could not parse --key value '{value}'. Expected 'ColumnName=Value', e.g. --key Id=5.";
                    return false;
                }
                options.KeyColumn = value[..eq];
                options.KeyValue = value[(eq + 1)..];
                break;

            default:
                error = $"Unknown option '{flag}'.";
                return false;
        }
    }
    return true;
}

static void PrintUsage()
{
    Console.WriteLine("Usage: LogCarver.Cli <server> <database> <schema.table> [--from <datetime>] [--to <datetime>] [--key <Column>=<Value>] [--undo]");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest --from \"2026-09-23T09:00\" --to \"2026-09-23T10:00\"");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest --key Id=5   (only this row's full history, 單筆資料歷史)");
    Console.WriteLine("Example: LogCarver.Cli localhost LPT_FullBak dbo.LogTest --key Id=5 --undo   (suggest SQL to reverse each event)");
    Console.WriteLine();
    Console.WriteLine("--from/--to filter which events are PRINTED to that incident window;");
    Console.WriteLine("reconstruction still uses the table's full observed history so before/after stay accurate.");
    Console.WriteLine("--key matches against either the before or after image of each event, so a row is found");
    Console.WriteLine("whether the column changed in that event or not.");
    Console.WriteLine("--undo prints a suggested SQL statement reversing each shown event. Its WHERE clause matches");
    Console.WriteLine("every observed column, not just a primary key, so it becomes a safe no-op if the row changed");
    Console.WriteLine("again since LogCarver saw it. Review before running - LogCarver never executes anything itself.");
    Console.WriteLine();
    Console.WriteLine("Connects with the current Windows account (integrated security). No data ever leaves this machine.");
}

static async Task<int> RunAsync(string server, string database, string tableName, CliOptions options)
{
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
        Console.WriteLine($"No schema found for '{tableName}'. Does it exist in {database}? Remember to include the schema, e.g. 'dbo.{tableName}'.");
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
    // just the [from,to] window or a --key filter - an UPDATE inside the
    // window (or for the requested row) still needs its "before" image,
    // which may have been written outside it. Filtering only affects what
    // gets printed below.
    var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns, transactionTimes);

    var toShow = history.Where(e =>
        (options.From is null || e.Timestamp is null || e.Timestamp >= options.From) &&
        (options.To is null || e.Timestamp is null || e.Timestamp <= options.To) &&
        (options.KeyColumn is null || RowEventFilter.MatchesKey(e, options.KeyColumn, options.KeyValue!)))
        .ToList();

    if (options.KeyColumn is not null)
    {
        Console.WriteLine($"{toShow.Count} row event(s) for {tableName} where {options.KeyColumn}={options.KeyValue}:");
        int unresolvable = history.Count(e => e.Before is null && e.After is null);
        if (unresolvable > 0)
            Console.WriteLine(
                $"Note: {unresolvable} other event(s) in this table could not be decoded (see refusals above) and were not checked against --key - " +
                "the row you're looking for may be among them, not necessarily absent.");
    }
    else if (options.From is not null || options.To is not null)
        Console.WriteLine($"{toShow.Count} of {history.Count} row event(s) for {tableName} fall in [{options.From}, {options.To}] (events with no resolvable timestamp are always shown):");
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

        if (options.ShowUndoSql)
        {
            var undoSql = UndoSqlGenerator.Generate(e, tableName);
            if (undoSql is not null)
                Console.WriteLine($"    UNDO: {undoSql}");
        }
    }

    return 0;
}

static string Format(IReadOnlyDictionary<string, object?> row) =>
    string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));

sealed class CliOptions
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? KeyColumn { get; set; }
    public string? KeyValue { get; set; }
    public bool ShowUndoSql { get; set; }
}
