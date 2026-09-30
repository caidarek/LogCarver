namespace LogCarver.Core.SqlServer;

public sealed record SnapshotRow(
    string PageId,
    int SlotId,
    IReadOnlyDictionary<string, object?>? Values,
    string AsOfLsn,
    string? Note)
{
    /// <summary>
    /// Mirrors RowEvent.NeedsManualReview - see that doc comment. Here it's
    /// always true (Note is only ever set, to either the source event's own
    /// Note or the "could not be decoded" fallback, exactly when Values is
    /// null), but deriving it the same way keeps the two types' semantics
    /// identical rather than relying on a caller remembering that.
    /// </summary>
    public bool NeedsManualReview => Note is not null;
}

public sealed record SnapshotResult(
    IReadOnlyList<SnapshotRow> Rows,
    int EventsWithUnresolvedTimestampIgnored);

/// <summary>
/// Reconstructs a table's row-by-row state as of a specific point in time
/// (研究紀錄 功能5: 快照). For each physical row slot, keeps whichever
/// history event was most recently in effect at that moment; a slot whose
/// latest such event is a DELETE is excluded - the row didn't exist yet
/// at that time.
///
/// Events with no resolvable timestamp cannot be safely placed before or
/// after <c>asOf</c> and are skipped rather than guessed at - the count
/// is reported so a caller knows the snapshot may be incomplete, not
/// silently confident. Likewise, a slot whose latest applicable event was
/// refused by the schema-drift guard (or is otherwise undecodable) is
/// still included in the result - excluding it would wrongly imply the
/// row didn't exist - but with null Values and an explanatory Note
/// instead of a guessed content.
/// </summary>
public static class SnapshotBuilder
{
    public static SnapshotResult BuildSnapshot(IReadOnlyList<RowEvent> historyInLsnOrder, DateTime asOf)
    {
        var latestByKey = new Dictionary<(string PageId, int SlotId), RowEvent>();
        int unresolvedTimestampCount = 0;

        foreach (var e in historyInLsnOrder)
        {
            if (e.Timestamp is null)
            {
                unresolvedTimestampCount++;
                continue;
            }
            if (e.Timestamp > asOf) continue;

            // historyInLsnOrder is chronological, so a later assignment for
            // the same slot always supersedes an earlier one.
            latestByKey[(e.PageId, e.SlotId)] = e;
        }

        var rows = new List<SnapshotRow>();
        foreach (var e in latestByKey.Values)
        {
            if (e.Kind == RowEventKind.Delete) continue;

            rows.Add(new SnapshotRow(
                e.PageId,
                e.SlotId,
                e.After,
                e.Lsn,
                e.After is null ? e.Note ?? "row state at this time could not be decoded" : null));
        }

        return new SnapshotResult(rows, unresolvedTimestampCount);
    }
}
