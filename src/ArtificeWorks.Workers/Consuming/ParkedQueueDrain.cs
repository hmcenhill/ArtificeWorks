using System.Text;
using System.Text.Json;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Messaging.DeadLetters;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// Turns <c>artifice.parked</c> into rows in <c>dead_letters</c> (8.3): consume, insert, ack.
/// <para>
/// <strong>Deliberately a different consumer from the main loop.</strong> The pipeline's handlers
/// must never run for a parked message — the whole reason it is parked is that running them
/// failed. This one does nothing but write the failure down.
/// </para>
/// <para>
/// <strong>And deliberately the most defensive code in the system.</strong> Every message it sees
/// is already known to be broken. A body that won't parse still gets a row — payload as text,
/// work order id null, the parse failure recorded — because a drain that throws on a poison
/// message re-creates the exact wedge 8.2 just fixed, one queue further along. The only thing it
/// refuses to swallow is a database failure, and even then it requeues rather than acking: the
/// parked queue is durable, has no TTL, and no other consumer, so leaving the message there is
/// the safe answer while Postgres is unwell.
/// </para>
/// </summary>
public sealed class ParkedQueueDrain : BackgroundService
{
    private readonly IRabbitMqConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ParkedQueueDrain> _logger;

    private IChannel? _channel;

    public ParkedQueueDrain(
        IRabbitMqConnection connection,
        IServiceScopeFactory scopeFactory,
        ILogger<ParkedQueueDrain> logger)
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await _connection.CreateChannelAsync(stoppingToken);

        // Idempotent with the consumer's own declare, so start order doesn't matter.
        await _channel.QueueDeclareAsync(
            queue: RetryConfiguration.ParkedQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: RetryConfiguration.ParkedQueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Parked-queue drain consuming {Queue}; failures become dead_letters rows.",
            RetryConfiguration.ParkedQueueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown.
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        var headers = eventArgs.BasicProperties.Headers;
        var eventType = HeaderString(headers, "x-original-routing-key") ?? eventArgs.RoutingKey;
        var correlationId = Guid.TryParse(eventArgs.BasicProperties.CorrelationId, out var parsed) ? parsed : Guid.Empty;

        using (_logger.BeginCorrelationScope(eventArgs.BasicProperties.CorrelationId))
        {
            var payload = SafeText(eventArgs.Body);
            var (workOrderId, parseError) = ExtractWorkOrderId(payload);

            var error = HeaderString(headers, "x-death-reason")
                ?? "Parked without a recorded reason.";

            if (parseError is not null)
            {
                // Not a failure of the drain — a fact about the message, and one worth keeping:
                // "we could not even read this" is the most useful thing the row can say.
                error = $"{error} (payload could not be parsed: {parseError})";
            }

            var letter = new DeadLetter(
                eventType,
                payload,
                correlationId,
                workOrderId,
                ReadAttempt(headers),
                Truncate(error),
                DateTime.UtcNow);

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<DeadLetterRepository>();
                await repository.Add(letter, eventArgs.CancellationToken);

                await _channel!.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, eventArgs.CancellationToken);

                _logger.LogWarning(
                    "Dead letter recorded: {EventType} for work order {WorkOrderId} after {Attempts} attempt(s) — {Error}",
                    eventType, workOrderId, letter.Attempts, letter.LastError);
            }
            catch (Exception e)
            {
                // The database, not the message. Requeue and slow down: the parked queue is the
                // durable store of last resort, and losing a message here would undo the whole
                // point of having it.
                _logger.LogError(e,
                    "Could not record a dead letter for {EventType}; leaving it parked and retrying.", eventType);

                await Task.Delay(TimeSpan.FromSeconds(1), eventArgs.CancellationToken);
                await _channel!.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true, eventArgs.CancellationToken);
            }
        }
    }

    /// <summary>
    /// Lifts the work order id out of the envelope so a failure can be shown next to the order it
    /// belongs to. Returns null plus a reason when the payload won't cooperate — never throws,
    /// because "unreadable" is a normal input here.
    /// </summary>
    private static (Guid? WorkOrderId, string? ParseError) ExtractWorkOrderId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("payload", out var inner)
                && inner.ValueKind == JsonValueKind.Object
                && inner.TryGetProperty("workOrderId", out var id)
                && id.TryGetGuid(out var workOrderId))
            {
                return (workOrderId, null);
            }

            return (null, null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>A body that isn't valid UTF-8 still has to end up in a text column somehow.</summary>
    private static string SafeText(ReadOnlyMemory<byte> body)
    {
        try
        {
            return Encoding.UTF8.GetString(body.Span);
        }
        catch (Exception)
        {
            return Convert.ToBase64String(body.Span);
        }
    }

    private static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is not null
            && headers.TryGetValue(RabbitMqConsumerService.AttemptHeader, out var raw)
            && raw is not null)
        {
            return raw switch
            {
                int value => value,
                long value => (int)value,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var value) => value,
                _ => 0
            };
        }

        return 0;
    }

    private static string? HeaderString(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value.ToString();
    }

    private static string Truncate(string error) => error.Length <= 2000 ? error : error[..1997] + "...";

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            await _channel.DisposeAsync();
            _channel = null;
        }
    }
}
