using ArtificeWorks.Application.Messaging;

namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// Handles one consumed integration event type. Exactly one handler is registered per
/// event type; the consumption plumbing (queue, dispatch, ack/nack) knows nothing about
/// any concrete handler, so adding a new one is a single DI registration and no plumbing
/// change. Handlers receive the full <see cref="EventEnvelope{T}"/> so they can read
/// metadata (correlation id, event id) alongside the payload.
/// </summary>
public interface IIntegrationEventHandler<TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(EventEnvelope<TEvent> envelope, CancellationToken cancellationToken);
}
