namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// Self-describing wrapper around every published event. Its metadata is what the
/// worker dispatches on and what the Epic 11 dashboard feed renders, so the shape
/// deliberately favours being self-describing over compact.
/// </summary>
/// <param name="EventId">Unique id for this specific message (idempotency key in Epic 8).</param>
/// <param name="EventType">The payload's <see cref="IntegrationEvent.EventType"/>; also the routing key.</param>
/// <param name="SchemaVersion">The payload's <see cref="IntegrationEvent.SchemaVersion"/>.</param>
/// <param name="CorrelationId">Ties every event and log line of one logical operation together.</param>
/// <param name="OccurredUtc">When the event was raised (UTC).</param>
/// <param name="Payload">The typed event.</param>
public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    Guid CorrelationId,
    DateTime OccurredUtc,
    T Payload) where T : IntegrationEvent;
