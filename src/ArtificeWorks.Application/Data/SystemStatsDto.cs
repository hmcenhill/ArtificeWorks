using ArtificeWorks.Application.Observability;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// The plain-JSON mirror of the metrics (9.2), served by <c>GET /system/stats</c>.
/// <para>
/// <strong>Why an endpoint as well as a meter.</strong> Epic 11's dashboard should not have to
/// speak PromQL, embed Grafana auth, or care whether a metrics backend is running at all. The
/// alternative — a React app querying Prometheus — pushes a query language, a CORS problem and an
/// auth problem into the frontend epic to save a few dozen lines over data that already exists.
/// </para>
/// <para>
/// It is served from the <em>same</em> <see cref="PipelineSnapshot"/> the observable gauges read
/// and the same tallies the counters increment, so this endpoint and Grafana can never disagree.
/// </para>
/// </summary>
public sealed record SystemStatsDto(
    DateTime AsOfUtc,
    bool Fresh,
    IReadOnlyDictionary<string, long> WorkOrdersByStatus,
    long WorkOrdersTotal,
    long WorkOrdersInFlight,
    long OutboxUnsent,
    double OutboxLagSeconds,
    long DeadLettersUnreplayed,
    long MessagesHandledSinceStart,
    long MessagesRetriedSinceStart,
    long MessagesParkedSinceStart,
    long MessagesReplayedSinceStart,
    long OutboxPublishedSinceStart)
{
    public static SystemStatsDto From(PipelineSnapshot snapshot, ArtificeWorksMetrics metrics) => new(
        snapshot.CapturedUtc,
        snapshot.IsFresh,
        snapshot.WorkOrdersByStatus,
        snapshot.TotalWorkOrders,
        snapshot.InFlight,
        snapshot.UnsentOutboxRows,
        snapshot.OutboxLagSeconds,
        snapshot.UnreplayedDeadLetters,
        metrics.MessagesHandledSinceStart,
        metrics.MessagesRetriedSinceStart,
        metrics.MessagesParkedSinceStart,
        metrics.MessagesReplayedSinceStart,
        metrics.OutboxPublishedSinceStart);
}
