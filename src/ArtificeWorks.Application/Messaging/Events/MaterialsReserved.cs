namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when the picking worker has successfully drawn every component a work order's BOM
/// calls for. This is the pipeline's hand-off from material picking (Epic 5) to production
/// (Epic 6): picking deliberately does <em>not</em> advance the order Scheduled → InProcess,
/// because starting production is Epic 6's transition to own — it consumes this event instead.
/// </summary>
/// <param name="Lines">What was actually taken off the shelf, for the audit trail and the dashboard feed.</param>
public sealed record MaterialsReserved(
    Guid WorkOrderId,
    string ProductId,
    uint Quantity,
    IReadOnlyList<ReservedComponent> Lines,
    DateTime ReservedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.materials-reserved";
}

/// <summary>One reserved component line on the wire.</summary>
public sealed record ReservedComponent(string ComponentId, uint Quantity);
