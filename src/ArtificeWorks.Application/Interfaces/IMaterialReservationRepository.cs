using ArtificeWorks.Application.Materials;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// The reservation write path. Deliberately narrow: the only mutating operation is a single
/// all-or-nothing commit, because "draw these components and record the pick" is the
/// transaction boundary the whole epic is built around — exposing a bare "decrement one
/// component" would invite callers to break atomicity.
/// <para>
/// Every operation here is scoped to a <em>build attempt</em> since 13.1. There is deliberately no
/// "the reservation for this order" query on the write path: an order can now have several picks,
/// so that question no longer has one answer, and a caller that asked it would be one refactor away
/// from picking twice for the same attempt. The read side ("everything ever drawn for this order")
/// is the timeline's, and it queries the tables directly.
/// </para>
/// </summary>
public interface IMaterialReservationRepository
{
    /// <summary>The existing pick for one attempt of a work order, if that attempt has been picked.</summary>
    Task<MaterialReservation?> GetForAttempt(
        Guid workOrderId,
        int attemptNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically draws every line of <paramref name="demand"/> from on-hand inventory and
    /// records the pick for <paramref name="attemptNumber"/>, or changes nothing at all.
    /// <para>
    /// Implementations must guarantee: (a) no component's on-hand can go below zero, even
    /// under concurrent picks; (b) if any line is short, every other line is left untouched;
    /// (c) a second call for the same work order <em>and attempt</em> reports
    /// <see cref="ReservationOutcome.AlreadyReserved"/> and draws nothing. A later attempt for the
    /// same order is a different pick and draws again — that is the point of 13.1.
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
        int attemptNumber,
        IReadOnlyList<ComponentDemand> demand,
        Func<MaterialReservation, Task>? stageWithReservation = null,
        CancellationToken cancellationToken = default);
}
