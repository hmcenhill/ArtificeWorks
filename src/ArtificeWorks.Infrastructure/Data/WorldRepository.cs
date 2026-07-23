using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// The world sweep's write path (10.4): restock the shelves, retire what nobody is watching, both
/// in one transaction.
/// <para>
/// <strong>A sweep, not a truncate.</strong> A truncate-and-reseed can delete the order a visitor
/// is watching, and it deletes the dead letters Epic 12 exists to show off. This restores the two
/// things that actually drift and refuses to touch anything else — which is what makes "without
/// downtime" fall out for free rather than being a thing to engineer: nothing in flight is
/// interrupted, because nothing in flight is in scope.
/// </para>
/// <para>
/// <strong>Restock before retire, and that order is load-bearing.</strong> This is the second bulk
/// writer against <c>components</c> — 5.2's picking is the first — and its component-ordering rule
/// exists to avoid deadlock. Picking takes component locks first and then writes a reservation; if
/// the sweep deleted orders (and their reservations) before touching components, the two would take
/// the same two resources in opposite orders and could deadlock under load. Doing components first
/// makes both writers agree on the sequence, and the <c>ORDER BY ... FOR UPDATE</c> below makes
/// them agree <em>within</em> components too. It looks like a pointless <c>ORDER BY</c> otherwise.
/// </para>
/// </summary>
public class WorldRepository : IWorldRepository
{
    private readonly ArtificeWorksDbContext _context;

    public WorldRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<WorldSweepCounts> Sweep(DateTime retireBeforeUtc, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var restocked = await RestockAsync(cancellationToken);
        var retired = await RetireAsync(retireBeforeUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new WorldSweepCounts(restocked, retired);
    }

    /// <summary>
    /// <c>on_hand = seed_on_hand</c>, for the shelves that are below it.
    /// <para>
    /// <strong>A conditional set, never a blind increment.</strong> Repeated sweeps have to be
    /// idempotent, and an increment that runs twice is a factory that mysteriously has more brass
    /// panels than it started with. The <c>&lt;</c> in the predicate also means the restock can only
    /// ever raise stock — so it cannot lower a shelf beneath an outstanding reservation's claim,
    /// which is the other half of 5.2's contract.
    /// </para>
    /// </summary>
    private Task<int> RestockAsync(CancellationToken cancellationToken) =>
        _context.Database.ExecuteSqlAsync($"""
            UPDATE components
            SET on_hand = seed_on_hand
            WHERE "ComponentId" IN (
                SELECT "ComponentId" FROM components
                WHERE on_hand < seed_on_hand
                ORDER BY "ComponentId"
                FOR UPDATE
            )
            """, cancellationToken);

    /// <summary>
    /// Removes work orders that finished or stalled long ago.
    /// <para>
    /// <strong>What is in scope, and what is emphatically not.</strong> Terminal orders (Completed,
    /// Cancelled) and stuck ones (OnHold, Fault) past the cutoff, and nothing else — an order in
    /// Intake…Delivery is never reset out from under a visitor, no matter how old. The catalog,
    /// <c>dead_letters</c> (Epic 12's exhibit) and <c>outbox_messages</c> (8.1's retention sweep
    /// already ages those out) are untouched.
    /// </para>
    /// <para>
    /// <strong>Held orders are retired, not rescued.</strong> The grooming decision that the
    /// simulation never releases a hold means something has to clear one eventually; time does, and
    /// a visitor's Release still beats the sweep to it. That is also what keeps 7.3's uncapped
    /// carrier refusal defensible.
    /// </para>
    /// <para>
    /// The cascade is the schema's, not this method's: state history, units, reservations and their
    /// lines, production and inspection runs, shipments and their lines all hang off
    /// <c>work_orders</c> with <c>ON DELETE CASCADE</c>, so one delete leaves no orphan behind.
    /// </para>
    /// </summary>
    private Task<int> RetireAsync(DateTime retireBeforeUtc, CancellationToken cancellationToken) =>
        _context.Database.ExecuteSqlAsync($"""
            DELETE FROM work_orders
            WHERE "CurrentStatus" IN ('Completed', 'Cancelled', 'OnHold', 'Fault')
              AND "UpdatedUtc" < {retireBeforeUtc}
            """, cancellationToken);
}
