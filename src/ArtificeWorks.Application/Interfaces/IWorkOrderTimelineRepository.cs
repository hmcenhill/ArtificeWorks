using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Production;
using ArtificeWorks.Domain.Models.Shipping;

namespace ArtificeWorks.Application.Interfaces;

public interface IWorkOrderTimelineRepository
{
    /// <summary>
    /// Everything persisted about one work order's journey, in one call. Read-only and
    /// untracked: the timeline is a narrative, never a write path.
    /// </summary>
    Task<TimelineData?> GetTimelineData(Guid workOrderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The raw material of a timeline, gathered from the five places the system already records
/// what happened. There is deliberately <strong>no event-log table</strong> behind this: a log
/// row written next to the work is the dual-write problem Epic 8's transactional outbox exists
/// to solve, and building one here would pre-empt that design with a worse version of it. Epic 8
/// persists events anyway, and an <c>event</c> kind can then be merged into the same array
/// without changing its shape.
/// </summary>
public sealed record TimelineData(
    WorkOrder WorkOrder,
    MaterialReservation? Reservation,
    IReadOnlyList<ProductionRun> ProductionRuns,
    IReadOnlyList<InspectionRun> InspectionRuns,
    Shipment? Shipment);
