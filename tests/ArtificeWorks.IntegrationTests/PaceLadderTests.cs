using System.Collections.Concurrent;
using System.Text.Json;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Testcontainers.RabbitMq;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 10.1's pace ladder, against a real RabbitMQ.
/// <para>
/// A real broker is the point, for the same reason it was at 8.2: the delay is a queue TTL, the
/// return path is a dead-letter exchange, and "did the message come back <em>under its original
/// routing key</em>, after roughly the right wait, with its headers intact?" is a question only the
/// broker can answer. The rungs are configured in hundreds of milliseconds so the test measures
/// behaviour rather than patience — it is the same code either way.
/// </para>
/// <para>
/// No database and no outbox: the dispatcher's decision to pace is proved in
/// <see cref="OutboxTests"/> where it can be raced against real Postgres. What is proved here is
/// the transport half — that a delay rung really does hold a message and really does hand it back
/// intact, and that a longer wait cannot block a shorter one behind it.
/// </para>
/// </summary>
[Collection(BrokerTestCollection.Name)]
public class PaceLadderTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.11").Build();
    private readonly RecordingHandler _handler = new();

    private IHost _host = null!;
    private PaceConfiguration _pace = null!;
    private RabbitMqRawPublisher _wire = null!;

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

            // The shipped ladder is 1s…34s. Three rungs of the same shape, measured in hundreds of
            // milliseconds so a test run stays a test run.
            ["Pace:RungsMs:0"] = "300",
            ["Pace:RungsMs:1"] = "1500",
            ["Pace:RungsMs:2"] = "3000",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.WorkerServiceName);

        builder.Services.AddRabbitMqConnection(builder.Configuration);
        builder.Services.Configure<PaceConfiguration>(builder.Configuration.GetSection(PaceConfiguration.SectionName));
        builder.Services.AddSingleton<SimulationSettingsCache>();
        builder.Services.AddSingleton<IPacePolicy, PacePolicy>();

        builder.Services.AddScoped<CorrelationContext>();
        builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        builder.Services.AddSingleton(_handler);
        builder.Services.AddEventConsumer();
        builder.Services.AddEventHandler<WorkOrderScheduled, RecordingHandlerAdapter>();

        _host = builder.Build();
        _pace = _host.Services.GetRequiredService<IOptions<PaceConfiguration>>().Value;
        _wire = _host.Services.GetRequiredService<RabbitMqRawPublisher>();

        await _host.StartAsync();
        await WaitForConsumer();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _rabbit.DisposeAsync();
    }

    /// <summary>
    /// The story's central claim: a paced event arrives at the handler after roughly its rung's
    /// TTL, and arrives <strong>as itself</strong> — same routing key, same correlation id, same
    /// body. If the routing key were lost the message would come back unroutable, which is exactly
    /// why a rung is an exchange rather than a routing key.
    /// </summary>
    [Fact]
    public async Task A_paced_event_is_delivered_after_its_rung_and_keeps_its_identity()
    {
        var workOrderId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var publishedUtc = DateTime.UtcNow;
        await PublishToRung(rung: 1, workOrderId, correlationId);   // 1500ms

        var handled = await Poll(() => _handler.For(workOrderId));

        Assert.NotNull(handled);
        Assert.Equal("work-order.scheduled", handled!.RoutingKey);
        Assert.Equal(correlationId, handled.CorrelationId);

        // Roughly the rung: the broker checks the head of the queue, so this is a floor with slack
        // above it, not a stopwatch. The floor is the assertion that matters — an unpaced message
        // arrives in milliseconds.
        var elapsed = handled.ReceivedUtc - publishedUtc;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(1_200),
            $"Expected at least the 1500ms rung; the message arrived after {elapsed.TotalMilliseconds:N0}ms.");
    }

    /// <summary>
    /// The head-of-line property the ladder exists to provide, and the whole reason there is one
    /// queue per duration rather than per-message TTL on a shared one.
    /// <para>
    /// A long message is published <em>first</em>; a short one second. RabbitMQ only ever checks
    /// the message at the head of a queue for expiry, so on a shared queue the 3s message would
    /// hold the 300ms message behind it. On separate queues they are delivered in <strong>rung
    /// order, not publish order</strong>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_orders_on_different_rungs_arrive_in_rung_order_not_publish_order()
    {
        var slow = Guid.NewGuid();
        var quick = Guid.NewGuid();

        await PublishToRung(rung: 2, slow, Guid.NewGuid());     // 3000ms, published first
        await PublishToRung(rung: 0, quick, Guid.NewGuid());    // 300ms, published second

        var quickHandled = await Poll(() => _handler.For(quick));
        Assert.NotNull(quickHandled);

        // The short one is through while the long one is still resting: publish order reversed.
        Assert.Null(_handler.For(slow));

        var slowHandled = await Poll(() => _handler.For(slow));
        Assert.NotNull(slowHandled);
        Assert.True(slowHandled!.ReceivedUtc > quickHandled!.ReceivedUtc);
    }

    /// <summary>
    /// Pacing off is the shipped default, and off has to mean the timing and the path are exactly
    /// what 8.1 shipped. Published straight to <c>artifice.events</c>, as an unpaced dispatcher
    /// does, and it must arrive at once.
    /// </summary>
    [Fact]
    public async Task An_unpaced_event_is_delivered_immediately()
    {
        var workOrderId = Guid.NewGuid();

        var publishedUtc = DateTime.UtcNow;
        await _wire.PublishRawAsync(
            "work-order.scheduled", Envelope(workOrderId, Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid());

        var handled = await Poll(() => _handler.For(workOrderId));

        Assert.NotNull(handled);

        var elapsed = handled!.ReceivedUtc - publishedUtc;
        Assert.True(elapsed < TimeSpan.FromMilliseconds(300),
            $"An unpaced message took {elapsed.TotalMilliseconds:N0}ms; pacing off must not add delay.");
    }

    /// <summary>
    /// The ladder is declared on connect by <em>every</em> host, not by the consumer — because the
    /// outbox dispatcher runs in all three and publishing to an exchange that does not exist closes
    /// the channel. This host never consumed a pace queue; the topology should still be complete.
    /// </summary>
    [Fact]
    public async Task The_pace_ladder_is_declared_on_connect()
    {
        await using var channel = await _host.Services
            .GetRequiredService<IRabbitMqConnection>().CreateChannelAsync();

        for (var rung = 0; rung < _pace.Rungs.Length; rung++)
        {
            // Passive-equivalent: declaring with matching arguments succeeds, and a queue that did
            // not exist would have been created rather than found — so the real proof is that a
            // publish to the rung's exchange (above) routes somewhere.
            var declared = await channel.QueueDeclarePassiveAsync(_pace.QueueFor(rung));
            Assert.Equal(_pace.QueueFor(rung), declared.QueueName);
        }
    }

    // -------------------------------------------------------------------------- helpers

    private Task PublishToRung(int rung, Guid workOrderId, Guid correlationId) =>
        _wire.PublishToAsync(
            exchange: _pace.ExchangeFor(rung),
            routingKey: "work-order.scheduled",
            payload: Envelope(workOrderId, correlationId),
            eventId: Guid.NewGuid(),
            correlationId: correlationId,
            pacedMs: _pace.MillisecondsFor(rung));

    private static string Envelope(Guid workOrderId, Guid correlationId) =>
        JsonSerializer.Serialize(
            new EventEnvelope<WorkOrderScheduled>(
                Guid.NewGuid(), "work-order.scheduled", 1, correlationId, DateTime.UtcNow,
                new WorkOrderScheduled(workOrderId, "PRD-PACE", "Paced Automaton", 1, DateTime.UtcNow)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// Declares and binds the work queue before any test publishes (idempotent with the consumer's
    /// own declare). A direct exchange silently drops a message with no matching binding, so
    /// publishing into a half-started consumer is a race the test would lose invisibly.
    /// </summary>
    private async Task WaitForConsumer()
    {
        await using var channel = await _host.Services
            .GetRequiredService<IRabbitMqConnection>().CreateChannelAsync();

        await channel.QueueDeclareAsync(
            RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, "work-order.scheduled");
    }

    private static async Task<T?> Poll<T>(Func<T?> read) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value)
            {
                return value;
            }
            await Task.Delay(50);
        }
        return null;
    }
}

/// <summary>
/// Records what arrived and when. Deliberately only an arrival <em>timestamp</em>: the fixture is
/// shared across the tests in this class, so each test measures its own delay from its own publish
/// rather than from a clock the handler started.
/// </summary>
public sealed class RecordingHandler
{
    private readonly ConcurrentDictionary<Guid, Received> _received = new();

    public Received? For(Guid workOrderId) => _received.TryGetValue(workOrderId, out var value) ? value : null;

    public void Record(Guid workOrderId, string routingKey, Guid correlationId) =>
        _received.TryAdd(workOrderId, new Received(routingKey, correlationId, DateTime.UtcNow));

    public sealed record Received(string RoutingKey, Guid CorrelationId, DateTime ReceivedUtc);
}

public sealed class RecordingHandlerAdapter : IIntegrationEventHandler<WorkOrderScheduled>
{
    private readonly RecordingHandler _recorder;

    public RecordingHandlerAdapter(RecordingHandler recorder)
    {
        _recorder = recorder;
    }

    public Task HandleAsync(EventEnvelope<WorkOrderScheduled> envelope, CancellationToken cancellationToken)
    {
        _recorder.Record(envelope.Payload.WorkOrderId, envelope.EventType, envelope.CorrelationId);
        return Task.CompletedTask;
    }
}
