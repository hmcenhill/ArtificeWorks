namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when the picking worker has successfully drawn every component a work order's BOM
/// calls for. This is the pipeline's hand-off from material picking (Epic 5) to production
/// (Epic 6): picking deliberately does <em>not</em> advance the order Scheduled → InProcess,
/// because starting production is Epic 6's transition to own — it consumes this event instead.
/// <para>
/// Since 13.1 an order can raise this more than once: every build attempt gets its own pick, so a
/// rebuild announces itself here exactly as the original build did.
/// </para>
/// </summary>
/// <param name="Quantity">How many finished units this pick bought parts for — the outstanding
/// quantity on a rebuild, not the order's total.</param>
/// <param name="AttemptNumber">The build attempt these materials are for. Carried rather than
/// inferred: the consumer starts production for this attempt, and hard-coding 1 stopped being safe
/// the moment a second pick became possible.</param>
/// <param name="Lines">What was actually taken off the shelf, for the audit trail and the dashboard feed.</param>
public sealed record MaterialsReserved(
    Guid WorkOrderId,
    string ProductId,
    uint Quantity,
    int AttemptNumber,
    IReadOnlyList<ReservedComponent> Lines,
    DateTime ReservedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.materials-reserved";
}

/// <summary>One reserved component line on the wire.</summary>
public sealed record ReservedComponent(string ComponentId, uint Quantity);
