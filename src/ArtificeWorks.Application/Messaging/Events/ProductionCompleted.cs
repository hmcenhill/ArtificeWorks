namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// Raised when a production attempt has built its units. Consumed by the inspection stage,
/// which judges exactly the units this attempt produced.
/// <para>
/// The event carries <see cref="AttemptNumber"/> rather than leaving it to be inferred: it is
/// the inspection stage's dedupe key (6.4), and inferring "which attempt was that?" from the
/// order's current state at handling time would be exactly the check-then-act race the key
/// exists to close.
/// </para>
/// </summary>
/// <param name="SerialNumbers">The units built by this attempt — the shortfall, not the whole order.</param>
public sealed record ProductionCompleted(
    Guid WorkOrderId,
    string ProductId,
    IReadOnlyList<Guid> SerialNumbers,
    int AttemptNumber,
    DateTime CompletedUtc) : IntegrationEvent
{
    public override string EventType => "work-order.production-completed";
}
