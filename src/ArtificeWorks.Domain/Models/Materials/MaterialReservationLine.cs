namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// One component drawn for a work order: how much of which component the pick took.
/// Holds the component id rather than a <see cref="Component"/> navigation — the reservation
/// is an immutable audit record of what was taken, not a live view of the catalog.
/// </summary>
public class MaterialReservationLine
{
    public Guid Id { get; }
    public Guid ReservationId { get; }
    public string ComponentId { get; }
    public uint Quantity { get; }

    private MaterialReservationLine() { }

    public MaterialReservationLine(MaterialReservation reservation, string componentId, uint quantity)
    {
        Id = Guid.NewGuid();
        ReservationId = reservation.Id;
        ComponentId = componentId;
        Quantity = quantity;
    }
}
