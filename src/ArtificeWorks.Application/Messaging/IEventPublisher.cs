namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// Publishes typed integration events to the message broker. The abstraction lives in
/// Application; the RabbitMQ implementation lives in Infrastructure. Callers publish a
/// bare event — the implementation wraps it in an <see cref="EventEnvelope{T}"/>, stamps
/// the correlation id from the ambient <see cref="ICorrelationContext"/>, and routes it
/// by the event's <see cref="IntegrationEvent.EventType"/>.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IntegrationEvent;
}
