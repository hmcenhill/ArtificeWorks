using System.Text.Json;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Messaging;

using Microsoft.Extensions.Options;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ArtificeWorks.Api.Realtime;

/// <summary>
/// The API's read-only observer of <c>artifice.events</c> (11.2). A hosted consumer on its own
/// <c>artifice.dashboard</c> queue, bound to <see cref="WorkOrderEventTypes.All"/>, that relays
/// every <c>work-order.*</c> event to browsers over the <see cref="DashboardHub"/>. It is the first
/// subscriber for <c>work-order.faulted</c> and <c>work-order.completed</c>, left orphaned since
/// Epics 7–8 for exactly this feed.
/// <para>
/// <strong>It is not a pipeline stage and must never behave like one.</strong>
/// </para>
/// <list type="bullet">
///   <item><description><strong>It always acks.</strong> A relay that failed to broadcast lost a
///     <em>screen update</em>, not a unit of work — there is nothing to retry and nothing to park.
///     It never touches 8.2's ladder; a throw is logged and the message is acked, because the
///     outbox/worker path is the durable one and the feed is allowed to miss a frame.</description></item>
///   <item><description><strong>Its queue is auto-delete with a message TTL.</strong> The feed is
///     live traffic, not history: a downed dashboard must not hoard a reconnect flood, and the queue
///     should vanish with the process. The opposite of the worker queue's durability, on purpose.</description></item>
///   <item><description><strong>It reads, it does not write.</strong> No <c>DbContext</c>, no outbox,
///     no state change. It deserializes just enough of the envelope to relay it.</description></item>
/// </list>
/// <para>
/// <strong>Single-instance assumption.</strong> One fixed-name queue shared by every API instance
/// would round-robin deliveries and each browser would see only a fraction. At the demo's
/// single-instance scale that cannot happen; a horizontally-scaled deployment would give each
/// instance its own queue and add a SignalR backplane (an Epic 15 concern).
/// </para>
/// </summary>
public sealed class DashboardRelay : BackgroundService
{
    /// <summary>The relay's own queue — separate from <c>artifice.workers</c>, so it never steals a message from the pipeline.</summary>
    public const string QueueName = "artifice.dashboard";

    /// <summary>Live traffic ages out fast: an event nobody relayed within this window is a stale frame, not a backlog.</summary>
    public const int MessageTtlMs = 60_000;

    /// <summary>How long to wait before retrying the initial broker connect. The feed stays dark meanwhile; the pipeline is unaffected.</summary>
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRabbitMqConnection _connection;
    private readonly IDashboardBroadcaster _broadcaster;
    private readonly RabbitMqConfiguration _config;
    private readonly ILogger<DashboardRelay> _logger;

    private IChannel? _channel;

    public DashboardRelay(
        IRabbitMqConnection connection,
        IDashboardBroadcaster broadcaster,
        IOptions<RabbitMqConfiguration> config,
        ILogger<DashboardRelay> logger)
    {
        _connection = connection;
        _broadcaster = broadcaster;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>The queue arguments, exposed so a test can declare the identical queue without a mismatch.</summary>
    public static IDictionary<string, object?> QueueArguments => new Dictionary<string, object?>
    {
        ["x-message-ttl"] = MessageTtlMs,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry the initial connect rather than crashing the API if the broker is briefly down at
        // startup: this is the demo's web frontend, and a dark feed is a better failure than a dead
        // site. Once connected, RabbitMQ.Client's automatic recovery keeps the consumer alive across
        // later blips.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SetUpAndConsumeAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                    "Dashboard relay could not start (broker unavailable?); retrying in {Delay}s. " +
                    "The feed is dark until it connects; the pipeline is unaffected.",
                    StartupRetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(StartupRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // The consumer runs on its own callbacks; hold the service open until shutdown.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown.
        }
    }

    private async Task SetUpAndConsumeAsync(CancellationToken stoppingToken)
    {
        // The shared connection declares artifice.events on first use; we own only this queue.
        _channel = await _connection.CreateChannelAsync(stoppingToken);

        await _channel.QueueDeclareAsync(
            queue: QueueName,
            durable: false,
            exclusive: false,
            autoDelete: true,
            arguments: QueueArguments,
            cancellationToken: stoppingToken);

        // Direct exchange, no wildcard: bind every published key explicitly. A new event type is
        // one line in WorkOrderEventTypes, and it appears on the feed with no change here.
        foreach (var routingKey in WorkOrderEventTypes.All)
        {
            await _channel.QueueBindAsync(
                queue: QueueName,
                exchange: _config.ExchangeName,
                routingKey: routingKey,
                cancellationToken: stoppingToken);
        }

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Dashboard relay consuming {Queue} (auto-delete, TTL {Ttl}ms), bound to [{Keys}] on {Exchange}.",
            QueueName, MessageTtlMs, string.Join(", ", WorkOrderEventTypes.All), _config.ExchangeName);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        try
        {
            if (TryRead(eventArgs, out var @event))
            {
                await _broadcaster.BroadcastAsync(@event, eventArgs.CancellationToken);
            }
        }
        catch (Exception e)
        {
            // A dropped broadcast is a dropped screen frame, not a lost unit. Log and move on —
            // never retry, never park. This is the entire temperament of the relay.
            _logger.LogWarning(e,
                "Dashboard relay could not broadcast {RoutingKey}; dropping the frame.", eventArgs.RoutingKey);
        }
        finally
        {
            // Always ack, even after a failure. There is nothing to redeliver: the durable path is
            // the worker's queue, and requeuing here would only replay a frame nobody is waiting for.
            if (_channel is { IsOpen: true })
            {
                await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, eventArgs.CancellationToken);
            }
        }
    }

    /// <summary>
    /// Reads the envelope metadata plus the payload's <c>workOrderId</c> — nothing more. The body is
    /// the same camelCase <c>EventEnvelope&lt;T&gt;</c> the publisher wrote; every payload names its
    /// order first, so one property read reaches it without knowing the concrete event type.
    /// </summary>
    private bool TryRead(BasicDeliverEventArgs eventArgs, out DashboardEvent @event)
    {
        @event = default!;
        try
        {
            using var document = JsonDocument.Parse(eventArgs.Body);
            var root = document.RootElement;

            var eventId = ReadGuid(root, "eventId") ?? Guid.Empty;
            var eventType = root.TryGetProperty("eventType", out var t) && t.GetString() is { } typed
                ? typed
                : eventArgs.RoutingKey;
            var correlationId = ReadGuid(root, "correlationId") ?? Guid.Empty;
            var occurredUtc = root.TryGetProperty("occurredUtc", out var o) && o.TryGetDateTime(out var when)
                ? when
                : DateTime.UtcNow;

            Guid? workOrderId = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                ? ReadGuid(payload, "workOrderId")
                : null;

            @event = new DashboardEvent(eventId, eventType, correlationId, occurredUtc, workOrderId);
            return true;
        }
        catch (Exception e)
        {
            // A body that won't parse is odd, but the relay has nothing to recover — the worker's
            // queue is where a genuinely poison message is handled. Log and let the ack drop it.
            _logger.LogWarning(e,
                "Dashboard relay could not read an envelope for {RoutingKey}; skipping the frame.", eventArgs.RoutingKey);
            return false;
        }
    }

    private static Guid? ReadGuid(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetGuid(out var parsed) ? parsed : null;

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
