using System.Text.Json;

using ArtificeWorks.Application.Messaging;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Wraps an event in an <see cref="EventEnvelope{T}"/>, stamps the ambient correlation id, and
/// hands it straight to the broker.
/// <para>
/// <strong>Since 8.1 this is not what application code gets.</strong> <c>IEventPublisher</c>
/// resolves to <see cref="Outbox.OutboxEventPublisher"/>, which writes a row in the caller's
/// transaction; the broker is reached only by the outbox dispatcher. This class stays for tests
/// and tools that genuinely want an unbuffered publish, and as the one place the envelope shape
/// and the wire format are defined together.
/// </para>
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher
{
    // Web defaults = camelCase, matching the API's JSON so the dashboard feed sees one shape.
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RabbitMqRawPublisher _wire;
    private readonly ICorrelationContext _correlation;

    public RabbitMqEventPublisher(RabbitMqRawPublisher wire, ICorrelationContext correlation)
    {
        _wire = wire;
        _correlation = correlation;
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

        return _wire.PublishRawAsync(
            envelope.EventType,
            JsonSerializer.Serialize(envelope, SerializerOptions),
            envelope.EventId,
            envelope.CorrelationId,
            headers: null,
            cancellationToken);
    }
}
