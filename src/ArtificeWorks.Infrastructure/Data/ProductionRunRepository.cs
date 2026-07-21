using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models.Production;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// The idempotent write path for a production attempt.
/// <para>
/// <strong>Why one SaveChanges matters.</strong> The caller has already changed the tracked work
/// order in memory — new serialized units, possibly a Scheduled → InProcess transition, a state
/// history entry. Adding the run row to the <em>same</em> context and saving once puts all of it
/// in a single implicit transaction. A duplicate delivery therefore doesn't just fail to record
/// a marker: its units and its transition roll back with the failed insert, so an attempt builds
/// exactly once even when two deliveries race past the caller's pre-check together.
/// </para>
/// <para>
/// This keeps the property 5.4 got for free — the dedupe marker and the work are one atomic
/// write — without an inbox table, which would be a second write that could drift from the work
/// it claims to describe.
/// </para>
/// </summary>
public class ProductionRunRepository : IProductionRunRepository
{
    private const string UniqueViolation = "23505";

    private readonly ArtificeWorksDbContext _context;

    public ProductionRunRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<bool> AttemptExists(Guid workOrderId, int attemptNumber, CancellationToken cancellationToken = default)
        => await _context.ProductionRuns
            .AsNoTracking()
            .AnyAsync(run => run.WorkOrderId == workOrderId && run.AttemptNumber == attemptNumber, cancellationToken);

    public async Task<bool> TryCommitAttempt(ProductionRun run, CancellationToken cancellationToken = default)
    {
        _context.ProductionRuns.Add(run);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException e) when (IsDuplicateAttempt(e))
        {
            // Another delivery of the same event committed this attempt first. Nothing of ours
            // was written. The caller returns immediately and the per-message DI scope is
            // disposed, so the still-Added entities in this context are never retried.
            return false;
        }
    }

    private static bool IsDuplicateAttempt(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
