using System.Diagnostics;
using System.Text;

using ArtificeWorks.Application.Observability;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// The wire itself: put these bytes on that exchange under this routing key. Deliberately knows
/// nothing about envelopes, events, or correlation contexts — which is what lets it be a
/// singleton, and is why the outbox dispatcher and 8.2's retry ladder (both background loops with
/// no ambient request) can use it directly.
/// <para>
/// <strong>It is also the only place trace context crosses into AMQP</strong> (9.1). One producer
/// span per publish, and a <c>traceparent</c> / <c>baggage</c> pair injected into the message
/// headers by the standard <see cref="TraceContextPropagator"/> rather than by hand-rolled
/// headers — so the consumer on the other side extracts them with the same standard code and the
/// hop renders as a parent/child edge instead of two unrelated traces.
/// </para>
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
        ActivityContext? parentContext = null,
        CancellationToken cancellationToken = default)
        => PublishToAsync(_config.ExchangeName, routingKey, payload, eventId, correlationId, headers,
            parentContext, pacedMs: null, cancellationToken);

    /// <summary>
    /// Publishes to a named exchange rather than the shared one. 8.2's ladder uses it to push a
    /// failed delivery onto a retry exchange, 10.1's pacing to drop an event onto a delay rung, and
    /// the empty string to route a parked message straight to a queue through the default exchange.
    /// </summary>
    /// <param name="parentContext">
    /// The trace to publish under when there is no ambient one — which is the outbox dispatcher's
    /// whole situation (9.1). Null means "use <see cref="Activity.Current"/>", which is what the
    /// retry ladder and the parked-queue republish want: they are already inside the consumer span
    /// for the delivery that failed, so their republish belongs under it.
    /// </param>
    /// <param name="pacedMs">How long the message will rest before delivery, for the span tag (10.1).</param>
    public async Task PublishToAsync(
        string exchange,
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        ActivityContext? parentContext = null,
        int? pacedMs = null,
        CancellationToken cancellationToken = default)
    {
        // Producer span. Named per the OTel messaging conventions so a Tempo waterfall reads as
        // "publish X → process X" rather than as two anonymous boxes.
        using var activity = parentContext is { } parent && parent != default
            ? ArtificeWorksTelemetry.ActivitySource.StartActivity(
                $"publish {routingKey}", ActivityKind.Producer, parent)
            : ArtificeWorksTelemetry.ActivitySource.StartActivity(
                $"publish {routingKey}", ActivityKind.Producer);

        activity?.SetTag(ArtificeWorksTelemetry.MessagingSystem, ArtificeWorksTelemetry.RabbitMq);
        activity?.SetTag(ArtificeWorksTelemetry.MessagingOperation, "publish");
        activity?.SetTag(ArtificeWorksTelemetry.MessagingDestination, exchange.Length == 0 ? "(default)" : exchange);
        activity?.SetTag(ArtificeWorksTelemetry.MessagingMessageId, eventId.ToString());
        activity?.SetTag(ArtificeWorksTelemetry.EventTypeAttribute, routingKey);
        activity?.SetTag(ArtificeWorksTelemetry.CorrelationIdAttribute, correlationId.ToString());

        // The gap this opens between producer and consumer spans is correct and should render as a
        // gap in the waterfall — a paced order really is resting. Tagging it is what makes that
        // gap self-explanatory rather than the first thing someone tries to debug (10.1).
        if (pacedMs is int paced)
        {
            activity?.SetTag(ArtificeWorksTelemetry.PacedMsAttribute, paced);
        }

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

        // Whatever the caller brought (8.2's x-attempt, 8.3's original routing key), plus the
        // propagation headers. Injected even when no listener is attached and `activity` is null:
        // Activity.Current may still be a real span from an upstream instrumentation.
        var outgoing = headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(headers);

        // The correlation id rides as baggage next to traceparent — the decision taken at
        // grooming. traceparent carries causality; this carries the id a human can read out, and
        // stamping it here means every publish path gets it without remembering to.
        var context = activity?.Context ?? Activity.Current?.Context ?? default;
        var baggage = Baggage.Current.SetBaggage(
            ArtificeWorksTelemetry.CorrelationBaggageKey, correlationId.ToString());

        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(context, baggage), outgoing,
            static (carrier, key, value) => carrier[key] = value);

        properties.Headers = outgoing;

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: cancellationToken);

        // Debug, not Information (9.3): at Information this line fires once per event per hop and
        // is one of the three things that were drowning the interesting lines. The stage
        // transitions it accompanies are logged by the workflow services.
        _logger.LogDebug(
            "Published {EventType} ({EventId}) [correlation {CorrelationId}] to {Exchange}",
            routingKey, eventId, correlationId, exchange.Length == 0 ? "(default exchange)" : exchange);
    }
}
