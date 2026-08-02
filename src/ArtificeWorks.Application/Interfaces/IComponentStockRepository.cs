namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// The other direction on the shelf. <see cref="IMaterialReservationRepository"/> draws stock down;
/// this puts it back up, and it exists because 13.3 gave the factory a second, entirely different
/// reason to move <c>components.on_hand</c>: a sub-assembly order finished and its units are stock.
/// <para>
/// Deliberately as narrow as its opposite number, and for the same reason — the transaction boundary
/// is the interesting part, not the arithmetic.
/// </para>
/// </summary>
public interface IComponentStockRepository
{
    /// <summary>
    /// Credits <paramref name="quantity"/> to a component's on-hand and commits whatever
    /// <paramref name="stageWithCredit"/> stages, in one transaction.
    /// <para>
    /// The credit and the child order's terminal transition <strong>must</strong> commit together:
    /// the transition is what makes a redelivery a no-op, so stock credited outside it could be
    /// credited twice. The work order's <c>xmin</c> concurrency token is what settles a genuine
    /// race — the loser's save throws, its credit rolls back with it, and the redelivery finds an
    /// order that is already Completed.
    /// </para>
    /// </summary>
    /// <returns>False when the component id names no shelf at all, in which case nothing is written.</returns>
    Task<bool> TryCredit(
        string componentId,
        uint quantity,
        Func<Task>? stageWithCredit = null,
        CancellationToken cancellationToken = default);
}
