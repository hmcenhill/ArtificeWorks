using System.Text.Json;

using ArtificeWorks.Application.Messaging;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ implementation of <see cref="IEventPublisher"/>. Wraps the event in an
/// <see cref="EventEnvelope{T}"/>, stamps the ambient correlation id, serializes as JSON,
/// and publishes to the shared direct exchange with routing key = event type.
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher
{
    // Web defaults = camelCase, matching the API's JSON so the dashboard feed sees one shape.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IRabbitMqConnection _connection;
    private readonly ICorrelationContext _correlation;
    private readonly RabbitMqConfiguration _config;
    private readonly ILogger<RabbitMqEventPublisher> _logger;

    public RabbitMqEventPublisher(
        IRabbitMqConnection connection,
        ICorrelationContext correlation,
        IOptions<RabbitMqConfiguration> config,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _connection = connection;
        _correlation = correlation;
        _config = config.Value;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default)
        where T : IntegrationEvent
    {
        var envelope = new EventEnvelope<T>(
            EventId: Guid.NewGuid(),
            EventType: @event.EventType,
            SchemaVersion: @event.SchemaVersion,
            CorrelationId: _correlation.CorrelationId,
            OccurredUtc: DateTime.UtcNow,
            Payload: @event);

        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);

        await using var channel = await _connection.CreateChannelAsync(cancellationToken);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = envelope.EventId.ToString(),
            CorrelationId = envelope.CorrelationId.ToString(),
            Type = envelope.EventType,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        await channel.BasicPublishAsync(
            exchange: _config.ExchangeName,
            routingKey: @event.EventType,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Published {EventType} ({EventId}) [correlation {CorrelationId}] to {Exchange}",
            envelope.EventType, envelope.EventId, envelope.CorrelationId, _config.ExchangeName);
    }
}
