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
    Task<ReservationCommitResult> TryReserve(
        Guid workOrderId,
        IReadOnlyList<ComponentDemand> demand,
        CancellationToken cancellationToken = default);
}
