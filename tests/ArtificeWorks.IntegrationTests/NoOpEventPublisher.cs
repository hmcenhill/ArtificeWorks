using ArtificeWorks.Application.Messaging;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Stand-in publisher for the API integration tests, which assert HTTP + persistence
/// behaviour and must not depend on a running broker. The real publish→consume path is
/// exercised against a Testcontainers RabbitMQ in story 4.2.
/// </summary>
internal sealed class NoOpEventPublisher : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default)
        where T : IntegrationEvent => Task.CompletedTask;
}
