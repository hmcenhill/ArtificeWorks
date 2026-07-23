namespace ArtificeWorks.Api.Realtime;

/// <summary>
/// The slim envelope the dashboard hub pushes to browsers — just enough of an
/// <c>EventEnvelope&lt;T&gt;</c> to update a card and render a feed line without a second fetch.
/// It is metadata only: no payload body, because the board reads authoritative state from
/// <c>GET /work-orders</c> and the detail from the timeline. The relay reads these fields off the
/// wire envelope; it never opens a <c>DbContext</c>.
/// </summary>
/// <param name="EventId">The envelope's id — a client can dedupe on it (publishing is at-least-once).</param>
/// <param name="EventType">The routing key, e.g. <c>work-order.faulted</c>; what the feed labels a line with.</param>
/// <param name="CorrelationId">Ties this to one logical operation; shown so a viewer can grep the logs.</param>
/// <param name="OccurredUtc">When the event was raised (UTC).</param>
/// <param name="WorkOrderId">The order the event names, so the board can update a single card. Null if the payload carried none.</param>
public sealed record DashboardEvent(
    Guid EventId,
    string EventType,
    Guid CorrelationId,
    DateTime OccurredUtc,
    Guid? WorkOrderId);
