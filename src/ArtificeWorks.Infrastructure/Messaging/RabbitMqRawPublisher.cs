using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// The wire itself: put these bytes on that exchange under this routing key. Deliberately knows
/// nothing about envelopes, events, or correlation contexts — which is what lets it be a
/// singleton, and is why the outbox dispatcher and 8.2's retry ladder (both background loops with
/// no ambient request) can use it directly.
/// </summary>
public sealed class RabbitMqRawPublisher : IBrokerPublisher
{
    private readonly IRabbitMqConnection _connection;
    private readonly RabbitMqConfiguration _config;
    private readonly ILogger<RabbitMqRawPublisher> _logger;

    public RabbitMqRawPublisher(
        IRabbitMqConnection connection,
        IOptions<RabbitMqConfiguration> config,
        ILogger<RabbitMqRawPublisher> logger)
    {
        _connection = connection;
        _config = config.Value;
        _logger = logger;
    }

    public Task PublishRawAsync(
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default)
        => PublishToAsync(_config.ExchangeName, routingKey, payload, eventId, correlationId, headers, cancellationToken);

    /// <summary>
    /// Publishes to a named exchange rather than the shared one. 8.2's ladder uses it to push a
    /// failed delivery onto a delay exchange, and the empty string to route a parked message
    /// straight to a queue through the default exchange.
    /// </summary>
    public async Task PublishToAsync(
        string exchange,
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        await using var channel = await _connection.CreateChannelAsync(cancellationToken);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = eventId.ToString(),
            CorrelationId = correlationId.ToString(),
            Type = routingKey,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        if (headers is not null && headers.Count > 0)
        {
            properties.Headers = headers;
        }

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Published {EventType} ({EventId}) [correlation {CorrelationId}] to {Exchange}",
            routingKey, eventId, correlationId, exchange.Length == 0 ? "(default exchange)" : exchange);
    }
}
