namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when a carrier has accepted a work order's finished units. The order is already in
/// Delivery — inspection put it there — so this announces the <em>parcel</em>, not a change of
/// manufacturing state.
/// <para>
/// Consumed by the dispatch stage, which hands the parcel over and completes the order.
/// </para>
/// </summary>
/// <param name="SerialNumbers">The units in the parcel: the passing ones, never the scrapped.</param>
public sealed record ShipmentScheduled(
    Guid WorkOrderId,
    string ProductId,
    string Carrier,
    string TrackingNumber,
    IReadOnlyList<Guid> SerialNumbers,
    DateTime EstimatedArrivalUtc,
    DateTime BookedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.shipment-scheduled";
}
