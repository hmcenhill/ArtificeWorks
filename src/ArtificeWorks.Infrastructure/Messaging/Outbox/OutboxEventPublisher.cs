using System.Text.Json;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Infrastructure.Messaging.Outbox;

/// <summary>
/// The publisher every handler and every API command gets since 8.1. Same
/// <see cref="IEventPublisher"/> interface, so no caller changed shape — but instead of reaching
/// for the broker it <c>Add</c>s an <see cref="OutboxMessage"/> to the caller's own
/// <see cref="ArtificeWorksDbContext"/> and returns.
/// <para>
/// <strong>It deliberately does not save.</strong> The row is left tracked so it is flushed by
/// whatever <c>SaveChanges</c> commits the work it describes — the same unit of work, the same
/// transaction. That is the entire point of the story: the state change and its event commit
/// together or not at all. Follow 6.4's <c>production_runs</c> precedent when adding a new
/// publish site: <em>stage the event before the save, never after it.</em>
/// </para>
/// <para>
/// Because the row is only tracked, a publish on a code path that then rolls back or simply
/// returns without saving leaves nothing behind — which is exactly right. The losing side of a
/// duplicate-delivery race announces nothing, because it did nothing.
/// </para>
/// </summary>
public sealed class OutboxEventPublisher : IEventPublisher, IRawEventPublisher
{
    private readonly ArtificeWorksDbContext _context;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<OutboxEventPublisher> _logger;

    public OutboxEventPublisher(
        ArtificeWorksDbContext context,
        ICorrelationContext correlation,
        ILogger<OutboxEventPublisher> logger)
    {
        _context = context;
        _correlation = correlation;
        _logger = logger;
    }

    public Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default)
        where T : IntegrationEvent
    {
        var envelope = new EventEnvelope<T>(
            EventId: Guid.NewGuid(),
            EventType: @event.EventType,
            SchemaVersion: @event.SchemaVersion,
            CorrelationId: _correlation.CorrelationId,
            OccurredUtc: DateTime.UtcNow,
            Payload: @event);

        // Serialized here, in the request/delivery that caused it, with the correlation id it was
        // caused under. The dispatcher publishes these bytes verbatim and never re-derives them.
        var payload = JsonSerializer.Serialize(envelope, RabbitMqEventPublisher.SerializerOptions);

        _context.OutboxMessages.Add(new OutboxMessage(
            envelope.EventId, envelope.EventType, envelope.CorrelationId, payload, envelope.OccurredUtc));

        _logger.LogInformation(
            "Queued {EventType} ({EventId}) [correlation {CorrelationId}] to the outbox.",
            envelope.EventType, envelope.EventId, envelope.CorrelationId);

        return Task.CompletedTask;
    }

    public Task EnqueueAsync(string eventType, string payload, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var eventId = Guid.NewGuid();

        _context.OutboxMessages.Add(new OutboxMessage(
            eventId, eventType, correlationId, payload, DateTime.UtcNow));

        _logger.LogInformation(
            "Queued raw {EventType} ({EventId}) [correlation {CorrelationId}] to the outbox.",
            eventType, eventId, correlationId);

        return Task.CompletedTask;
    }
}
