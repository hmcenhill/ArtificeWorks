namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when the <em>full ordered quantity</em> has passed inspection and the order has
/// advanced to Delivery. Not raised per unit and not raised on a partial pass — the order-level
/// outcome is derived by counting passing units against the ordered quantity.
/// <para>
/// Nothing binds this routing key yet: Epic 7's shipping consumer is its first subscriber,
/// exactly as Epic 5 left <c>work-order.materials-reserved</c> waiting for this epic.
/// </para>
/// </summary>
public sealed record InspectionPassed(
    Guid WorkOrderId,
    string ProductId,
    IReadOnlyList<Guid> SerialNumbers,
    DateTime PassedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.inspection-passed";
}
