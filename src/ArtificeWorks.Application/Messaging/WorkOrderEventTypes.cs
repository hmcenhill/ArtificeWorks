namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// The full set of routing keys the factory publishes to <c>artifice.events</c> — the honest
/// inventory of what it announces, in one place. A new event type is <strong>one line here</strong>,
/// not a hunt: this is what the Epic 11 dashboard relay binds its queue to, so the feed shows
/// everything the factory says — including the announcements (<c>created</c>, <c>faulted</c>,
/// <c>completed</c>) the pipeline itself never consumes.
/// <para>
/// Because <c>artifice.events</c> is a <em>direct</em> exchange there is no <c>work-order.*</c>
/// wildcard; a subscriber that wants them all must name them all. Drift between this list and the
/// actual <see cref="IntegrationEvent.EventType"/> set is caught by a unit test, not by hope.
/// </para>
/// </summary>
public static class WorkOrderEventTypes
{
    public const string Created = "work-order.created";
    public const string Scheduled = "work-order.scheduled";
    public const string MaterialsReserved = "work-order.materials-reserved";
    public const string ProductionCompleted = "work-order.production-completed";
    public const string ReworkRequired = "work-order.rework-required";
    public const string InspectionPassed = "work-order.inspection-passed";
    public const string ShipmentScheduled = "work-order.shipment-scheduled";
    public const string Faulted = "work-order.faulted";
    public const string Completed = "work-order.completed";

    /// <summary>Every published routing key. The dashboard relay binds each one explicitly.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Created,
        Scheduled,
        MaterialsReserved,
        ProductionCompleted,
        ReworkRequired,
        InspectionPassed,
        ShipmentScheduled,
        Faulted,
        Completed,
    ];
}
