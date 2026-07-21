namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when inspection leaves a work order short of its ordered quantity: some units were
/// scrapped, the order has gone back to InProcess, and the shortfall needs rebuilding.
/// <para>
/// This is the event that makes the rework loop a genuine cycle over the bus rather than a
/// method call — the production consumer binds it alongside
/// <see cref="MaterialsReserved"/>, and both funnel into the same service entry point.
/// </para>
/// </summary>
/// <param name="AttemptNumber">The attempt that just failed. The rebuild is <c>AttemptNumber + 1</c>,
/// derived rather than carried so a redelivery computes the same key and collides.</param>
/// <param name="OutstandingQty">How many units still need building.</param>
public sealed record ReworkRequired(
    Guid WorkOrderId,
    string ProductId,
    IReadOnlyList<ScrappedUnit> Scrapped,
    uint OutstandingQty,
    int AttemptNumber,
    DateTime RequiredUtc) : IntegrationEvent
{
    public override string EventType => "work-order.rework-required";
}

/// <summary>One scrapped serialized unit and why it failed, on the wire.</summary>
public sealed record ScrappedUnit(Guid SerialNumber, string Reason);
