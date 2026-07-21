using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// The concurrency-safe reservation write path.
/// <para>
/// <strong>Why raw SQL.</strong> A read-modify-write decrement (load component, check on-hand,
/// subtract, save) is wrong under concurrency: two workers can read the same on-hand, both
/// decide there is enough, and both write — overselling the shelf. Instead each line is drawn
/// with a single atomic conditional statement:
/// </para>
/// <code>UPDATE components SET on_hand = on_hand - @qty WHERE "ComponentId" = @id AND on_hand >= @qty</code>
/// <para>
/// Postgres evaluates the predicate and applies the subtraction under the row lock it takes,
/// so the check and the decrement cannot be separated. Zero rows affected means "not enough",
/// and it means it authoritatively. This keeps the guard in the database and needs no
/// optimistic-concurrency rowversion on any aggregate (the codebase deliberately has none —
/// see the standing HANDOFF decision).
/// </para>
/// <para>
/// <strong>All-or-nothing.</strong> Every line is drawn inside one transaction; the first line
/// that comes up short aborts and rolls the whole thing back, so a partially-available BOM
/// leaves on-hand exactly as it was. Lines are drawn in a deterministic id order (established
/// by <see cref="Product.ComputeDemand"/>) so two concurrent multi-line reservations take
/// their row locks in the same sequence and can't deadlock against each other.
/// </para>
/// <para>
/// <strong>Idempotency.</strong> The reservation insert commits in that same transaction, and
/// the unique index on <c>WorkOrderId</c> makes a second pick for the same order impossible.
/// A duplicate delivery that races past the caller's pre-check therefore fails the insert, and
/// the rollback takes its decrements with it — inventory is drawn exactly once.
/// </para>
/// </summary>
public class MaterialReservationRepository : IMaterialReservationRepository
{
    private const string UniqueViolation = "23505";

    private readonly ArtificeWorksDbContext _context;

    public MaterialReservationRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<MaterialReservation?> GetForWorkOrder(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        return await _context.MaterialReservations
            .Include(reservation => reservation.Lines)
            .FirstOrDefaultAsync(reservation => reservation.WorkOrderId == workOrderId, cancellationToken);
    }

    public async Task<ReservationCommitResult> TryReserve(
        Guid workOrderId,
        IReadOnlyList<ComponentDemand> demand,
        CancellationToken cancellationToken = default)
    {
        if (demand.Count == 0)
        {
            throw new ArgumentException("Cannot reserve an empty demand.", nameof(demand));
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var line in demand)
        {
            // uint doesn't round-trip to a Postgres parameter type; the column is bigint
            // (Npgsql's mapping for uint), so compare and subtract in the same width.
            var quantity = (long)line.Quantity;

            var rowsAffected = await _context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE components
                SET on_hand = on_hand - {quantity}
                WHERE "ComponentId" = {line.ComponentId} AND on_hand >= {quantity}
                """,
                cancellationToken);

            if (rowsAffected == 0)
            {
                // Either the component is short or it doesn't exist. Both mean this order
                // cannot be picked; roll back so no earlier line stays decremented.
                await transaction.RollbackAsync(cancellationToken);
                return ReservationCommitResult.Short([line.ComponentId]);
            }
        }

        var reservation = new MaterialReservation(workOrderId, demand);
        _context.MaterialReservations.Add(reservation);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (IsDuplicateReservation(e))
        {
            // Another delivery of the same scheduling event got there first. Rolling back
            // undoes this attempt's decrements, so the winner's pick stands alone.
            await transaction.RollbackAsync(cancellationToken);

            // The failed insert leaves the reservation graph tracked as Added; detach it so a
            // later SaveChanges on this scope (the caller still writes state history) doesn't
            // retry it.
            foreach (var reservationLine in reservation.Lines)
            {
                _context.Entry(reservationLine).State = EntityState.Detached;
            }
            _context.Entry(reservation).State = EntityState.Detached;

            return ReservationCommitResult.AlreadyReserved();
        }

        return ReservationCommitResult.Reserved(reservation);
    }

    private static bool IsDuplicateReservation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
