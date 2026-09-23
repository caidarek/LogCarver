namespace LogCarver.Core.SqlServer;

/// <summary>One row from fn_dblog, narrowed to the columns RowDecoder needs.</summary>
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
    byte[]? RowLogContents1);
