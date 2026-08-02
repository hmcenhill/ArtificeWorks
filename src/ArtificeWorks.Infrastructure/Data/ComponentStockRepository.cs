using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// Putaway (13.3): the shelf going up rather than down.
/// <para>
/// <strong>The same idiom as the draw, mirrored.</strong> 5.3 decrements with a single atomic
/// conditional <c>UPDATE</c> so the check and the subtraction cannot be separated; a credit needs no
/// predicate — stock only ever goes up — but it must still be one statement rather than a
/// read-modify-write, or two children finishing at once would both read the same on-hand and one
/// putaway would vanish. 10.4's restock takes the same shape for the same reason.
/// </para>
/// <para>
/// <strong>Why a transaction at all, when it is one statement.</strong> Because it is not: the
/// caller stages the child's Delivery → Completed transition and its announcement inside it, and
/// that transition is the thing that makes a redelivered <c>inspection-passed</c> a no-op. Credit
/// without transition would be stock added twice; transition without credit would be a
/// sub-assembly order that finished and produced nothing.
/// </para>
/// </summary>
public class ComponentStockRepository : IComponentStockRepository
{
    private readonly ArtificeWorksDbContext _context;

    public ComponentStockRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<bool> TryCredit(
        string componentId,
        uint quantity,
        Func<Task>? stageWithCredit = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // uint doesn't round-trip to a Postgres parameter type; the column is bigint (Npgsql's
        // mapping for uint), so add in the same width the draw subtracts in.
        var amount = (long)quantity;

        var rowsAffected = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE components
            SET on_hand = on_hand + {amount}
            WHERE "ComponentId" = {componentId}
            """,
            cancellationToken);

        if (rowsAffected == 0)
        {
            // No such component. Nothing is written and nothing is staged — the caller reports it
            // rather than completing an order whose output went nowhere.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (stageWithCredit is not null)
        {
            await stageWithCredit();
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }
}
