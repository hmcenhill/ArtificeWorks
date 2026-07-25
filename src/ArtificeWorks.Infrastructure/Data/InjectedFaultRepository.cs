using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// The injected-fault registry's read/write path (Epic 12) — arm, fire-once, and disarm leftovers.
/// </summary>
public class InjectedFaultRepository : IInjectedFaultRepository
{
    private readonly ArtificeWorksDbContext _context;

    public InjectedFaultRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<InjectedFault> Arm(
        Guid workOrderId, InjectedFaultKind kind, string armedBy, CancellationToken cancellationToken = default)
    {
        // Idempotent by (order, kind): a double-clicking visitor re-arming the same fault gets the
        // one they already have rather than a second row that would fire on a later attempt.
        var existing = await _context.InjectedFaults
            .FirstOrDefaultAsync(
                f => f.WorkOrderId == workOrderId && f.Kind == kind && f.FiredUtc == null,
                cancellationToken);

        if (existing is not null)
        {
            return existing.ToFault();
        }

        var row = InjectedFaultRow.Arm(workOrderId, kind, armedBy);
        _context.InjectedFaults.Add(row);
        await _context.SaveChangesAsync(cancellationToken);

        return row.ToFault();
    }

    public async Task<bool> TryConsume(
        Guid workOrderId, InjectedFaultKind kind, CancellationToken cancellationToken = default)
    {
        // A single conditional UPDATE is the fire-once guarantee: only a row that is still unfired
        // can be marked, and `FOR UPDATE SKIP LOCKED` means two concurrent consumers cannot both
        // claim the same fault. It runs as its own statement — not inside whatever transaction the
        // caller opens around the stage's work — so the mark survives even when that stage rolls
        // back, which is the property 12.2's transient fault turns on.
        var affected = await _context.Database.ExecuteSqlAsync($"""
            UPDATE injected_faults
            SET "FiredUtc" = {DateTime.UtcNow}
            WHERE "Id" = (
                SELECT "Id" FROM injected_faults
                WHERE "WorkOrderId" = {workOrderId}
                  AND "Kind" = {kind.ToString()}
                  AND "FiredUtc" IS NULL
                ORDER BY "ArmedUtc"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            """, cancellationToken);

        return affected > 0;
    }

    public Task<int> DisarmUnfired(CancellationToken cancellationToken = default) =>
        _context.Database.ExecuteSqlAsync($"""
            DELETE FROM injected_faults WHERE "FiredUtc" IS NULL
            """, cancellationToken);
}
