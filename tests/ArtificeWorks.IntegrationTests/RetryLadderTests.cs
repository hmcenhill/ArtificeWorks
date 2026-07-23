using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RabbitMQ.Client;

using Testcontainers.RabbitMq;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The retry ladder, poison handling and the parked queue (8.2), against a real RabbitMQ.
/// <para>
/// A real broker is the point here in a way it wasn't for 8.1: the delays are queue TTLs, the
/// return path is a dead-letter exchange, and "did the message actually come back under its
/// original routing key?" is a question only the broker can answer. The ladder is configured with
/// short rungs so the test measures behaviour rather than patience — it is the same code either
/// way.
/// </para>
/// <para>
/// No database: the handler under test is a scripted one whose whole job is to fail on cue. The
/// dedupe keys that make a retry safe are proved where they can be raced
/// (<see cref="MaterialPickingTests"/>, <see cref="ProductionInspectionTests"/>); what is proved
/// here is that a failure gets another chance, and that nothing is ever silently dropped again.
/// </para>
/// </summary>
[Collection(BrokerTestCollection.Name)]
public class RetryLadderTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.11").Build();
    private readonly ScriptedHandler _handler = new();

    private IHost _host = null!;
    private RetryConfiguration _retry = null!;

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();

        var amqp = new Uri(_rabbit.GetConnectionString());
        var userInfo = amqp.UserInfo.Split(':', 2);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMqConfiguration:Host"] = amqp.Host,
            ["RabbitMqConfiguration:Port"] = amqp.Port.ToString(),
            ["RabbitMqConfiguration:Username"] = Uri.UnescapeDataString(userInfo[0]),
            ["RabbitMqConfiguration:Password"] = Uri.UnescapeDataString(userInfo.Length > 1 ? userInfo[1] : string.Empty),
            ["RabbitMqConfiguration:VirtualHost"] = "/",
            ["RabbitMqConfiguration:ExchangeName"] = Exchange,

            // The shipped ladder is 5s / 30s / 2m. Same three rungs, same code path, measured in
            // hundreds of milliseconds so a test run stays a test run.
            ["Retry:DelaysMs:0"] = "300",
            ["Retry:DelaysMs:1"] = "600",
            ["Retry:DelaysMs:2"] = "900",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Telemetry, registered exactly as the worker host registers it (9.1). No OTLP endpoint is
        // configured, so nothing leaves the process — but the ActivitySource, the meter and the
        // metrics recorder every service now depends on are all real.
        builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.WorkerServiceName);

        builder.Services.AddRabbitMqConnection(builder.Configuration);
        builder.Services.AddScoped<CorrelationContext>();
        builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        builder.Services.AddSingleton(_handler);
        builder.Services.AddEventConsumer();
        builder.Services.AddEventHandler<WorkOrderScheduled, ScriptedHandlerAdapter>();

        _host = builder.Build();
        _retry = _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RetryConfiguration>>().Value;

        await _host.StartAsync();

        // The consumer declares the whole topology on start; give it a moment to finish before a
        // test publishes into it, so nothing is dropped by an exchange with no binding yet.
        await WaitForConsumer();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _rabbit.DisposeAsync();
    }

    [Fact]
    public async Task A_transient_failure_retries_with_increasing_backoff_and_then_succeeds()
    {
        var workOrderId = Guid.NewGuid();
        _handler.FailNextDeliveries(2);

        await Publish(workOrderId);

        // Delivery 1 fails → 300ms rung. Delivery 2 fails → 600ms rung. Delivery 3 succeeds.
        var handled = await Poll(() => _handler.Succeeded.FirstOrDefault(h => h.WorkOrderId == workOrderId));

        Assert.NotNull(handled);
        Assert.Equal(3, handled!.Attempt);
        Assert.Equal(3, _handler.DeliveriesFor(workOrderId));

        // Nothing parked: the failure was transient and the ladder did its job.
        Assert.Equal(0u, await MessageCount(RetryConfiguration.ParkedQueueName));
    }

    [Fact]
    public async Task A_retry_exhausted_message_parks_and_stops()
    {
        var workOrderId = Guid.NewGuid();
        _handler.FailNextDeliveries(int.MaxValue);

        await Publish(workOrderId);

        var parked = await Poll(() => ReadParked());
        Assert.NotNull(parked);

        // Three rungs means three retries and four deliveries in all.
        Assert.Equal(_retry.MaxAttempts + 1, _handler.DeliveriesFor(workOrderId));

        // The parked message still knows what it was and what it belonged to — 8.3 replays from
        // exactly this, with no archaeology.
        Assert.Equal("work-order.scheduled", parked!.RoutingKey);
        Assert.Equal(workOrderId, parked.WorkOrderId);
        Assert.Equal(_retry.MaxAttempts + 1, parked.Attempt);
        Assert.Contains("ladder is exhausted", parked.Reason);

        // And it stays stopped: nothing re-delivers it after parking.
        await Task.Delay(1_000);
        Assert.Equal(_retry.MaxAttempts + 1, _handler.DeliveriesFor(workOrderId));
    }

    [Fact]
    public async Task A_poison_message_parks_immediately_and_does_not_block_the_queue_behind_it()
    {
        // A body that is valid JSON but not an envelope. It will fail identically on every
        // delivery, so burning the ladder on it would be three pointless waits.
        await PublishRaw("work-order.scheduled", """{"nonsense": true, "payload": 42}""");

        var parked = await Poll(() => ReadParked());
        Assert.NotNull(parked);
        Assert.Contains("permanently unhandleable", parked!.Reason);

        // Attempt 1: it never touched a rung.
        Assert.Equal(1, parked.Attempt);

        // The real claim: with prefetch 1, a poison message that requeued would stall everything
        // behind it forever. The next message must be handled normally.
        var workOrderId = Guid.NewGuid();
        await Publish(workOrderId);

        var handled = await Poll(() => _handler.Succeeded.FirstOrDefault(h => h.WorkOrderId == workOrderId));
        Assert.NotNull(handled);
        Assert.Equal(1, handled!.Attempt);
    }

    [Fact]
    public async Task An_event_type_with_no_handler_parks_rather_than_disappearing()
    {
        // Bound routing keys and registered handlers are supposed to be the same list. If they
        // ever drift, the message must end up somewhere a human can see it — acking it away
        // silently is the failure mode this whole epic exists to remove.
        await using var channel = await NewChannel();
        await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, "work-order.invented");

        await PublishRaw("work-order.invented", """{"eventId":"00000000-0000-0000-0000-000000000000"}""");

        var parked = await Poll(() => ReadParked());

        Assert.NotNull(parked);
        Assert.Equal("work-order.invented", parked!.RoutingKey);
        Assert.Contains("No handler is registered", parked.Reason);
    }

    // -------------------------------------------------------------------------- helpers

    private Task Publish(Guid workOrderId)
    {
        var envelope = new EventEnvelope<WorkOrderScheduled>(
            Guid.NewGuid(), "work-order.scheduled", 1, Guid.NewGuid(), DateTime.UtcNow,
            new WorkOrderScheduled(workOrderId, "PRD-RETRY", "Retry Automaton", 1, DateTime.UtcNow));

        return PublishRaw("work-order.scheduled",
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private async Task PublishRaw(string routingKey, string payload)
    {
        var wire = _host.Services.GetRequiredService<RabbitMqRawPublisher>();
        await wire.PublishRawAsync(routingKey, payload, Guid.NewGuid(), Guid.NewGuid());
    }

    private async Task<IChannel> NewChannel()
        => await _host.Services.GetRequiredService<IRabbitMqConnection>().CreateChannelAsync();

    private async Task<ParkedMessage?> ReadParked()
    {
        await using var channel = await NewChannel();
        var message = await channel.BasicGetAsync(RetryConfiguration.ParkedQueueName, autoAck: true);
        if (message is null)
        {
            return null;
        }

        var headers = message.BasicProperties.Headers ?? new Dictionary<string, object?>();

        Guid? workOrderId = null;
        try
        {
            using var document = JsonDocument.Parse(message.Body);
            if (document.RootElement.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("workOrderId", out var id)
                && id.TryGetGuid(out var parsed))
            {
                workOrderId = parsed;
            }
        }
        catch (JsonException)
        {
            // A parked message may well be unparseable — that is often why it is parked.
        }

        return new ParkedMessage(
            RoutingKey: HeaderString(headers, "x-original-routing-key") ?? message.RoutingKey,
            Attempt: headers.TryGetValue(RabbitMqConsumerService.AttemptHeader, out var raw) && raw is int a ? a : 0,
            Reason: HeaderString(headers, "x-death-reason") ?? string.Empty,
            WorkOrderId: workOrderId);
    }

    private static string? HeaderString(IDictionary<string, object?> headers, string key) =>
        headers.TryGetValue(key, out var value) && value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value as string;

    private async Task<uint> MessageCount(string queue)
    {
        await using var channel = await NewChannel();
        return await channel.MessageCountAsync(queue);
    }

    /// <summary>
    /// Declares and binds the topology ourselves before any test publishes (idempotent with the
    /// consumer's own declare on startup). A <em>direct</em> exchange silently drops a message
    /// with no matching binding, so publishing into a half-started consumer is a race the test
    /// would lose invisibly rather than loudly.
    /// </summary>
    private async Task WaitForConsumer()
    {
        await using var channel = await NewChannel();

        await channel.QueueDeclareAsync(
            RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, "work-order.scheduled");

        await channel.QueueDeclareAsync(
            RetryConfiguration.ParkedQueueName, durable: true, exclusive: false, autoDelete: false);
    }

    private static async Task<T?> Poll<T>(Func<Task<T?>> read) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null)
            {
                return value;
            }
            await Task.Delay(100);
        }
        return null;
    }

    private static Task<T?> Poll<T>(Func<T?> read) where T : class => Poll(() => Task.FromResult(read()));

    private sealed record ParkedMessage(string RoutingKey, int Attempt, string Reason, Guid? WorkOrderId);
}

/// <summary>A handler that fails on cue, so a test can arrange a transient fault rather than wait for one.</summary>
public sealed class ScriptedHandler
{
    private readonly ConcurrentDictionary<Guid, int> _deliveries = new();
    private readonly ConcurrentQueue<Handled> _succeeded = new();
    private int _failuresRemaining;

    public IReadOnlyList<Handled> Succeeded => _succeeded.ToList();

    public void FailNextDeliveries(int count) => Interlocked.Exchange(ref _failuresRemaining, count);

    public int DeliveriesFor(Guid workOrderId) => _deliveries.TryGetValue(workOrderId, out var count) ? count : 0;

    public void Handle(Guid workOrderId)
    {
        var attempt = _deliveries.AddOrUpdate(workOrderId, 1, (_, existing) => existing + 1);

        if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
        {
            // An ordinary exception: not poison, so the consumer must treat it as transient.
            throw new InvalidOperationException($"Scripted transient failure on attempt {attempt}.");
        }

        _succeeded.Enqueue(new Handled(workOrderId, attempt));
    }

    public sealed record Handled(Guid WorkOrderId, int Attempt);
}

/// <summary>Adapts the scripted handler onto the real handler interface, so the real loop runs.</summary>
public sealed class ScriptedHandlerAdapter : IIntegrationEventHandler<WorkOrderScheduled>
{
    private readonly ScriptedHandler _scripted;

    public ScriptedHandlerAdapter(ScriptedHandler scripted)
    {
        _scripted = scripted;
    }

    public Task HandleAsync(EventEnvelope<WorkOrderScheduled> envelope, CancellationToken cancellationToken)
    {
        _scripted.Handle(envelope.Payload.WorkOrderId);
        return Task.CompletedTask;
    }
}
