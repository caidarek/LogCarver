# LogCarver

SQL Server transaction log parser — reads insert/update/delete history straight from the transaction log, without requiring Audit/CDC/Change Tracking to have been enabled beforehand.

**Status: working MVP.** Connects to a live SQL Server instance, decodes a table's full INSERT/UPDATE (before/after)/DELETE history, guards against the schema-drift trap (decoding old rows with a newer table structure), and supports filtering to an incident time window. See [`docs/原理說明.md`](docs/原理說明.md) for a plain-language walkthrough of how it works.

## Scope (current)

- SQL Server only, connects to a live instance via `fn_dblog`
- Runs entirely locally — no network calls, no data ever leaves the machine it runs on
- Explicitly detected and refused rather than guessed at: compressed tables (ROW/PAGE), off-row LOB values, records predating a schema-changing DDL
- Not yet implemented: offline LDF file analysis (recovering data `fn_dblog` can no longer see but that hasn't been physically overwritten — the tool's real differentiator, planned for later)

## Usage

```
dotnet run --project src/LogCarver.Cli -- <server> <database> <schema.table> [<from>] [<to>]
```

`<from>`/`<to>` (optional, e.g. `"2026-09-23T09:00"`) filter which events are *printed* to that window; reconstruction always uses the table's full observed history so before/after values stay accurate even when the window is narrow.

## Building a standalone executable

```
dotnet publish src/LogCarver.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
```

Produces a single ~81MB `LogCarver.Cli.exe` with no .NET installation required on the target machine (verified against Windows Server 2012 through 2025 and Windows 10/11 per the [.NET 10 supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)). `IncludeNativeLibrariesForSelfExtract` is required — without it, `Microsoft.Data.SqlClient`'s native SNI dependency ships as a separate DLL alongside the exe instead of being embedded.

## Testing

```
dotnet test
```

Runs both `LogCarver.Core.Tests` (pure decode logic, no database needed) and `LogCarver.Core.IntegrationTests` (exercises real `fn_dblog` behavior against `localhost`; needs a local SQL Server and creates/drops its own sandbox databases, all prefixed `LogCarver_`).

## License

MIT — see [LICENSE](LICENSE).
