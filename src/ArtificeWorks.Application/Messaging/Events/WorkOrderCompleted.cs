namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// The last event in the chain: the parcel is with the carrier and the work order is Completed.
/// <para>
/// <strong>One event, not two.</strong> A <c>shipment-dispatched</c> alongside this would say the
/// same thing at the same instant, because completion is automatic once a shipment is dispatched
/// — the visitor's decision is <em>which carrier</em>, not <em>whether to finish</em>.
/// </para>
/// <para>
/// Nothing binds it. It is the terminal announcement, published for Epic 11's dashboard, in the
/// same spirit as <c>work-order.faulted</c>.
/// </para>
/// </summary>
public sealed record WorkOrderCompleted(
    Guid WorkOrderId,
    string ProductId,
    string Carrier,
    string TrackingNumber,
    IReadOnlyList<Guid> SerialNumbers,
    DateTime CompletedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.completed";
}
