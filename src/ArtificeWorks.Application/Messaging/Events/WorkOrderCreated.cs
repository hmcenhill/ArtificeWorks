namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when a work order is created (enters <c>Intake</c>). First event in a work
/// order's story on the event feed.
/// </summary>
public sealed record WorkOrderCreated(
    Guid WorkOrderId,
    string ProductId,
    string ProductName,
    uint Quantity,
    string Requestor,
    DateTime CreatedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.created";
}
