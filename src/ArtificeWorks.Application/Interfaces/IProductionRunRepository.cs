using ArtificeWorks.Domain.Models.Production;

namespace ArtificeWorks.Application.Interfaces;

public interface IProductionRunRepository
{
    /// <summary>
    /// The cheap duplicate pre-check. It is <em>not</em> the guarantee — two deliveries can
    /// both pass it concurrently — <see cref="TryCommitAttempt"/> is what enforces
    /// once-per-attempt.
    /// </summary>
    Task<bool> AttemptExists(Guid workOrderId, int attemptNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a whole production attempt in one <c>SaveChanges</c>: the run row that is the
    /// attempt's dedupe key, plus everything the caller already changed on the tracked work
    /// order (its new units, its status, its history entry). Returns <c>false</c> when the
    /// attempt already exists, in which case nothing at all was written.
    /// </summary>
    Task<bool> TryCommitAttempt(ProductionRun run, CancellationToken cancellationToken = default);
}
