using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models.Shipping;
using ArtificeWorks.Infrastructure.Messaging.Outbox;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// The idempotent write path for booking a parcel. Simpler than Epic 6's run repositories,
/// because the thing that must happen exactly once here is the <em>order</em> again: shipping
/// runs once per work order, so the shipment row itself is the dedupe key and Epic 5's trick
/// applies unchanged.
/// <para>
/// The shipment, its lines and the work order's state-history note all commit in one
/// <c>SaveChanges</c>, so a losing duplicate takes its whole batch down with it rather than
/// leaving a stray note behind.
/// </para>
/// </summary>
public class ShipmentRepository : IShipmentRepository
{
    private const string UniqueViolation = "23505";

    private readonly ArtificeWorksDbContext _context;

    public ShipmentRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<Shipment?> GetForWorkOrder(Guid workOrderId, CancellationToken cancellationToken = default)
        => await _context.Shipments
            .Include(shipment => shipment.Lines)
            .FirstOrDefaultAsync(shipment => shipment.WorkOrderId == workOrderId, cancellationToken);

    public async Task<bool> TryBook(Shipment shipment, CancellationToken cancellationToken = default)
    {
        _context.Shipments.Add(shipment);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException e) when (IsDuplicateShipment(e))
        {
            // Another delivery booked this order first. Detach the failed graph so a later
            // SaveChanges on this scope doesn't retry the insert. Materialise the lines first:
            // detaching one triggers EF's fixup, which mutates the navigation we'd be iterating.
            foreach (var line in shipment.Lines.ToList())
            {
                _context.Entry(line).State = EntityState.Detached;
            }
            _context.Entry(shipment).State = EntityState.Detached;

            // And the announcement of a booking that didn't happen (8.1). The caller returns
            // straight after this, so nothing would flush it — but leaving a staged event behind
            // a failed write is the kind of thing that becomes true later by accident.
            foreach (var staged in _context.ChangeTracker.Entries<OutboxMessage>()
                         .Where(entry => entry.State == EntityState.Added)
                         .ToList())
            {
                staged.State = EntityState.Detached;
            }

            return false;
        }
    }

    public Task Update(CancellationToken cancellationToken = default)
        // The shipment was loaded by this same scoped context, so the change tracker already
        // sees the dispatch (and any transition on the order alongside it); just flush.
        => _context.SaveChangesAsync(cancellationToken);

    private static bool IsDuplicateShipment(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
