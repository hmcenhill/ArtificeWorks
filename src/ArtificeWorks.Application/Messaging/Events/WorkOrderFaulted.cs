namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when an order has exhausted its rebuild attempts and been routed to Fault. This is
/// the loop's bound: no further <see cref="ReworkRequired"/> follows it, so the cycle stops.
/// <para>
/// Announced rather than merely logged because Fault is a recoverable state a human is expected
/// to act on — the dashboard (Epic 11) needs to surface it, and nothing else in the pipeline
/// will move this order again on its own.
/// </para>
/// </summary>
/// <param name="AttemptNumber">The last attempt made before giving up.</param>
public sealed record WorkOrderFaulted(
    Guid WorkOrderId,
    string ProductId,
    string Reason,
    int AttemptNumber,
    DateTime FaultedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.faulted";
}
