namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when a work order advances Intake → Scheduled. There is no separate "schedule"
/// action in the state machine, so this is emitted when an advance lands the order in
/// <c>Scheduled</c>. This is the event Epic 5's material-picking workflow will consume.
/// </summary>
public sealed record WorkOrderScheduled(
    Guid WorkOrderId,
    string ProductId,
    string ProductName,
    uint Quantity,
    DateTime ScheduledUtc) : IntegrationEvent
{
    public override string EventType => "work-order.scheduled";
}
