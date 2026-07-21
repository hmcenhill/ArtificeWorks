using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models.Production;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// The idempotent write path for inspecting one build attempt — the mirror of
/// <see cref="ProductionRunRepository"/>, and it works the same way for the same reason: the
/// per-unit verdicts, the run row, and the order-level transition those verdicts caused all
/// commit in one <c>SaveChanges</c>, so a losing duplicate takes its whole decision with it.
/// </summary>
public class InspectionRunRepository : IInspectionRunRepository
{
    private const string UniqueViolation = "23505";

    private readonly ArtificeWorksDbContext _context;

    public InspectionRunRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<bool> AttemptExists(Guid workOrderId, int attemptNumber, CancellationToken cancellationToken = default)
        => await _context.InspectionRuns
            .AsNoTracking()
            .AnyAsync(run => run.WorkOrderId == workOrderId && run.AttemptNumber == attemptNumber, cancellationToken);

    public async Task<bool> TryCommitInspection(InspectionRun run, CancellationToken cancellationToken = default)
    {
        _context.InspectionRuns.Add(run);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException e) when (IsDuplicateAttempt(e))
        {
            return false;
        }
    }

    private static bool IsDuplicateAttempt(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
