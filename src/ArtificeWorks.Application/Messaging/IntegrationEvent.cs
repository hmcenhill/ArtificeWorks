namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// Base for a typed integration event — the payload carried inside an
/// <see cref="EventEnvelope{T}"/>. Each event declares a stable <see cref="EventType"/>
/// used as the RabbitMQ routing key and as the discriminator the dashboard event feed
/// (Epic 11) renders. Keep names self-describing and stable: renaming one is a wire
/// break, so treat <see cref="EventType"/> as a published contract.
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>
    /// Stable, self-describing event name, e.g. <c>work-order.scheduled</c>.
    /// Doubles as the routing key on the <c>artifice.events</c> exchange.
    /// </summary>
    public abstract string EventType { get; }

    /// <summary>
    /// Payload schema version. Bump when the shape changes incompatibly so consumers
    /// can branch on it (copied onto the envelope so a consumer can read it without
    /// deserializing the payload).
    /// </summary>
    public virtual int SchemaVersion => 1;
}
