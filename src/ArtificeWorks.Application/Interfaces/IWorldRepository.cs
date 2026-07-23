namespace ArtificeWorks.Application.Interfaces;

/// <param name="ComponentsRestocked">Component shelves returned to their seed level.</param>
/// <param name="OrdersRetired">Old terminal, held or faulted orders removed, cascades included.</param>
public sealed record WorldSweepCounts(int ComponentsRestocked, int OrdersRetired);

/// <summary>
/// The two things that drift in a permanently-running factory, put back (10.4).
/// </summary>
public interface IWorldRepository
{
    /// <summary>
    /// Restocks components to their seed levels and retires orders older than
    /// <paramref name="retireBeforeUtc"/>, in <strong>one transaction</strong> — a half-reset world
    /// is worse than an unreset one.
    /// </summary>
    Task<WorldSweepCounts> Sweep(DateTime retireBeforeUtc, CancellationToken cancellationToken = default);
}
