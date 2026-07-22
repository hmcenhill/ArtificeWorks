using ArtificeWorks.Domain.Models.Shipping;

namespace ArtificeWorks.Application.Interfaces;

public interface IShipmentRepository
{
    /// <summary>
    /// The order's shipment, with its lines, or null. Doubles as the cheap duplicate pre-check —
    /// not the guarantee; the unique index on <c>shipments.work_order_id</c> is.
    /// </summary>
    Task<Shipment?> GetForWorkOrder(Guid workOrderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a booking and everything tracked alongside it (the order's history note) in one
    /// <c>SaveChanges</c>. Returns <c>false</c> when another delivery booked this order first —
    /// the losing insert's whole batch rolls back with it, which is 5.4's best property kept.
    /// </summary>
    Task<bool> TryBook(Shipment shipment, CancellationToken cancellationToken = default);

    /// <summary>Flushes changes to an already-tracked shipment (a dispatch, a void).</summary>
    Task Update(CancellationToken cancellationToken = default);
}
