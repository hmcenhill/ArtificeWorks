namespace ArtificeWorks.Application.Observability;

/// <summary>
/// The database-derived half of the observability picture, taken all at once (9.2).
/// <para>
/// Everything here is a <em>right now</em> question — how many orders sit in each status, how far
/// the outbox has fallen behind, how many dead letters nobody has looked at — and every one of them
/// costs a query. Naively that is a query per gauge per scrape, issued from a metrics callback that
/// runs in root scope and cannot resolve a scoped <c>DbContext</c> in the first place. One snapshot
/// on a timer answers both problems, and means <c>GET /system/stats</c> and Grafana are reading the
/// same numbers rather than two independently-taken readings that disagree.
/// </para>
/// </summary>
public sealed record PipelineSnapshot(
    DateTime CapturedUtc,
    IReadOnlyDictionary<string, long> WorkOrdersByStatus,
    long UnsentOutboxRows,
    // Age in seconds of the oldest unsent outbox row; 0 when there is none. THE lag number.
    double OutboxLagSeconds,
    long UnreplayedDeadLetters,
    long TotalWorkOrders)
{
    /// <summary>What the gauges report before the first refresh has run — all zeros, never null.</summary>
    public static PipelineSnapshot Empty { get; } =
        new(DateTime.MinValue, new Dictionary<string, long>(), 0, 0, 0, 0);

    /// <summary>Orders that are neither Completed nor Cancelled — work the factory still owes.</summary>
    public long InFlight => WorkOrdersByStatus
        .Where(entry => entry.Key is not ("Completed" or "Cancelled"))
        .Sum(entry => entry.Value);

    /// <summary>True once a refresh has actually happened, so a caller can tell zeros from "not yet".</summary>
    public bool IsFresh => CapturedUtc != DateTime.MinValue;
}

/// <summary>
/// Holds the latest <see cref="PipelineSnapshot"/>. A singleton written by
/// <c>PipelineSnapshotService</c> and read by the observable gauges and <c>/system/stats</c>.
/// <para>
/// Deliberately trivial: a single reference swap. Readers never block, never see a torn value
/// (the snapshot is an immutable record), and get a stale-but-consistent picture rather than a
/// fresh-but-interleaved one.
/// </para>
/// </summary>
public sealed class PipelineSnapshotCache
{
    private PipelineSnapshot _current = PipelineSnapshot.Empty;

    public PipelineSnapshot Current => Volatile.Read(ref _current);

    public void Update(PipelineSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
