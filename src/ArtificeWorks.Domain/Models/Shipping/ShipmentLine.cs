namespace ArtificeWorks.Domain.Models.Shipping;

/// <summary>
/// One serialized unit in a parcel. Mirrors <see cref="Materials.MaterialReservationLine"/>:
/// it holds the serial number rather than a navigation to the unit, because the shipment is an
/// immutable record of what went out, not a live view of the order's stock.
/// <para>
/// It matters more than it looks. Once an order has scrapped some units and rebuilt others, the
/// question "which units actually shipped?" has a non-obvious answer, and this is where it lives.
/// </para>
/// </summary>
public class ShipmentLine
{
    public Guid Id { get; }
    public Guid ShipmentId { get; }
    public Guid SerialNumber { get; }

    private ShipmentLine() { }

    public ShipmentLine(Shipment shipment, Guid serialNumber)
    {
        Id = Guid.NewGuid();
        ShipmentId = shipment.Id;
        SerialNumber = serialNumber;
    }
}
