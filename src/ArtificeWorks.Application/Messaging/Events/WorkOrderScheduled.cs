namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when a work order advances Intake → Scheduled. There is no separate "schedule"
/// action in the state machine, so this is emitted when an advance lands the order in
/// <c>Scheduled</c>. This is the event Epic 5's material-picking workflow consumes.
/// <para>
/// Since 13.3 it has a <strong>second publisher</strong>: when a child work order completes, the
/// parent it was building for is released and re-scheduled with this same key, so it re-picks
/// through the trigger that already exists rather than through a new "resume" verb. That is why the
/// payload now carries the attempt and the outstanding quantity explicitly — a parent released after
/// a failed rebuild pick is not asking for attempt 1 or for its whole ordered quantity, and the
/// handler must not have to re-read the order's state to find out (the rule 6.4 set and 13.1
/// extended to picking: a redelivery has to compute an identical request, or the unique index it
/// collides on is guarding two different things).
/// </para>
/// </summary>
/// <param name="Quantity">
/// How many finished units this scheduling buys parts for — the order's outstanding quantity, which
/// for a freshly created order is simply everything it ordered.
/// </param>
/// <param name="AttemptNumber">
/// The build attempt this pick supplies: 1 for a new order, N for a parent resuming a rebuild whose
/// pick came up short.
/// </param>
public sealed record WorkOrderScheduled(
    Guid WorkOrderId,
    string ProductId,
    string ProductName,
    uint Quantity,
    int AttemptNumber,
    DateTime ScheduledUtc) : IntegrationEvent
{
    public override string EventType => "work-order.scheduled";
}
