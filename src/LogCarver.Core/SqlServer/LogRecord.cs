namespace LogCarver.Core.SqlServer;

/// <summary>One row from fn_dblog, narrowed to the columns RowDecoder needs.</summary>
/// <param name="PossiblyCorrupted">
/// Always false for a record read via fn_dblog (SQL Server's own log-reading
/// code has already fixed up anything an offline byte-level reader would
/// see corrupted). An offline reader (see LogCarverOffline's
/// LogRecordAdapter) sets this when the record's bytes crossed a physical
/// artifact it can detect but not repair - callers should treat any
/// decoded value from a record with this set as unverified, not silently
/// trustworthy.
/// </param>
public sealed record LogRecord(
    string Lsn,
    string Operation,
    string Context,
    int? OffsetInRow,
    string? AllocUnitName,
    string? PageId,
    int? SlotId,
    string? TransactionId,
    byte[]? RowLogContents0,
    byte[]? RowLogContents1,
    bool PossiblyCorrupted = false);
