# LogCarver

SQL Server transaction log parser — reads insert/update/delete history straight from the transaction log, without requiring Audit/CDC/Change Tracking to have been enabled beforehand.

**Status: early development.** Core decoding approach has been validated against a real SQL Server 2025 instance; this repository is the C# implementation and is not yet functional.

## Scope (current)

- SQL Server only (connects to a live instance via `fn_dblog`)
- Runs entirely locally — no network calls, no data ever leaves the machine it runs on
- Not yet supported: off-row LOB values, compressed tables, offline LDF file analysis

## License

MIT — see [LICENSE](LICENSE).
