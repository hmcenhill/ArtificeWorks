using ArtificeWorks.Application.Materials;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// The reservation write path. Deliberately narrow: the only mutating operation is a single
/// all-or-nothing commit, because "draw these components and record the pick" is the
/// transaction boundary the whole epic is built around — exposing a bare "decrement one
/// component" would invite callers to break atomicity.
/// </summary>
public interface IMaterialReservationRepository
{
    /// <summary>The existing pick for a work order, if it has already been picked.</summary>
    Task<MaterialReservation?> GetForWorkOrder(Guid workOrderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically draws every line of <paramref name="demand"/> from on-hand inventory and
    /// records the pick, or changes nothing at all.
    /// <para>
    /// Implementations must guarantee: (a) no component's on-hand can go below zero, even
    /// under concurrent picks; (b) if any line is short, every other line is left untouched;
    /// (c) a second call for the same work order reports
    /// <see cref="ReservationOutcome.AlreadyReserved"/> and draws nothing.
    /// </para>
    /// </summary>
    /// <param name="stageWithReservation">
    /// Ran <em>inside</em> the reservation transaction, after the draw succeeds and before the
    /// commit, so anything it stages on the same unit of work commits with the pick. 8.1 added it
    /// for the outbox row: the announcement of a pick must not be able to exist without the pick,
    /// nor the pick without the announcement. It also closes 5.2's smaller caveat — the
    /// state-history note is now inside the transaction rather than a second save behind it.
    /// Not called on the short or duplicate paths, because on those nothing happened to announce.
    /// </param>
    Task<ReservationCommitResult> TryReserve(
        Guid workOrderId,
        IReadOnlyList<ComponentDemand> demand,
        Func<MaterialReservation, Task>? stageWithReservation = null,
        CancellationToken cancellationToken = default);
}
