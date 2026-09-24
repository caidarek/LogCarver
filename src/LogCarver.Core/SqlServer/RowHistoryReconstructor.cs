namespace LogCarver.Core.SqlServer;

public enum RowEventKind { Insert, Delete, Update }

public sealed record RowEvent(
    string Lsn,
    RowEventKind Kind,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After,
    string? Note,
    DateTime? Timestamp);

/// <summary>
/// Walks a table's log records in LSN order and reconstructs each
/// physical row slot's history, applying RowPatcher to UPDATE diffs
/// against the most recently known image of that slot.
///
/// Row identity: grouped by (Page ID, Slot ID), not by primary key or by
/// record order. A MODIFY_ROW diff generally does not touch the primary
/// key, so it cannot be used to identify which row changed; physical
/// location is what SQL Server itself uses to mean "the same row" for an
/// in-place update, and is the only thing every record type actually
/// carries. (The research prototype paired records positionally, which
/// only works because it drove requests through one connection in a known
/// order - that assumption does not hold against real, concurrent
/// workloads and is not repeated here.)
/// </summary>
public static class RowHistoryReconstructor
{
    public static IReadOnlyList<RowEvent> Reconstruct(
        IEnumerable<LogRecord> recordsInLsnOrder,
        IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<string> ddlBoundaryLsns,
        IReadOnlyDictionary<string, DateTime>? transactionBeginTimes = null)
    {
        var events = new List<RowEvent>();

        DateTime? TimestampOf(LogRecord r) =>
            r.TransactionId is not null && (transactionBeginTimes?.TryGetValue(r.TransactionId, out var t) ?? false)
                ? t
                : null;

        // Per physical slot: the most recently known row image and the LSN
        // it was actually written at. The write LSN - not the LSN of
        // whatever record we're currently looking at - is what the
        // schema-drift guard must check: a row untouched since before a
        // metadata-only DDL (e.g. ADD COLUMN) is still physically in the
        // old layout even if we're now looking at it from a later point
        // in the log.
        var state = new Dictionary<(string PageId, int SlotId), (byte[] Bytes, string WrittenAtLsn)>();

        foreach (var record in recordsInLsnOrder)
        {
            if (record.PageId is null || record.SlotId is null) continue;
            var key = (record.PageId, record.SlotId.Value);

            switch (record.Operation)
            {
                case "LOP_INSERT_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
                    {
                        var decoded = TryDecode(bytes, schema, record.Lsn, ddlBoundaryLsns, out var note);
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Insert, null, decoded, note, TimestampOf(record)));
                        state[key] = (bytes, record.Lsn);
                        break;
                    }

                case "LOP_DELETE_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
                    {
                        var decoded = TryDecode(bytes, schema, record.Lsn, ddlBoundaryLsns, out var note);
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Delete, decoded, null, note, TimestampOf(record)));
                        state.Remove(key);
                        break;
                    }

                case "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS":
                    {
                        if (record.OffsetInRow is not int offset ||
                            record.RowLogContents0 is not { } rlc0 ||
                            record.RowLogContents1 is not { } rlc1)
                        {
                            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                                "diff not available (missing offset or RowLog Contents)", TimestampOf(record)));
                            break;
                        }

                        if (!state.TryGetValue(key, out var before))
                        {
                            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                                "before image unknown - this row's insert (or a prior update) is outside the observed log window", TimestampOf(record)));
                            break;
                        }

                        byte[] afterBytes;
                        try
                        {
                            afterBytes = RowPatcher.ApplyForward(before.Bytes, offset, rlc0, rlc1);
                        }
                        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                        {
                            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                                $"patch failed - before image and this diff do not line up ({ex.Message})", TimestampOf(record)));
                            break;
                        }

                        var beforeDecoded = TryDecode(before.Bytes, schema, before.WrittenAtLsn, ddlBoundaryLsns, out var beforeNote);
                        var afterDecoded = TryDecode(afterBytes, schema, record.Lsn, ddlBoundaryLsns, out var afterNote);
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Update, beforeDecoded, afterDecoded, beforeNote ?? afterNote, TimestampOf(record)));
                        state[key] = (afterBytes, record.Lsn);
                        break;
                    }
            }
        }

        return events;
    }

    private static IReadOnlyDictionary<string, object?>? TryDecode(
        byte[] bytes, IReadOnlyList<ColumnSchema> schema, string lsn, IReadOnlyList<string> ddlBoundaryLsns, out string? note)
    {
        try
        {
            note = null;
            return RowDecoder.Decode(bytes, schema, lsn, ddlBoundaryLsns);
        }
        catch (Exception ex) when (ex is SchemaDriftException or NotSupportedException or UnsupportedRowFormatException)
        {
            note = ex.Message;
            return null;
        }
    }
}
