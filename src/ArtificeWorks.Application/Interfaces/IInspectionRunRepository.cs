using ArtificeWorks.Domain.Models.Production;

namespace ArtificeWorks.Application.Interfaces;

public interface IInspectionRunRepository
{
    /// <summary>Cheap duplicate pre-check; see <see cref="IProductionRunRepository.AttemptExists"/>.</summary>
    Task<bool> AttemptExists(Guid workOrderId, int attemptNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a whole inspection of one build attempt in a single <c>SaveChanges</c>: the run
    /// row (the dedupe key), the per-unit verdicts, and whatever order-level transition those
    /// verdicts caused. Returns <c>false</c> when the attempt was already inspected.
    /// </summary>
    Task<bool> TryCommitInspection(InspectionRun run, CancellationToken cancellationToken = default);
}
