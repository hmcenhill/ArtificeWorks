using System.Text.Json;

using ArtificeWorks.Api.Realtime;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Infrastructure.Messaging;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RabbitMQ.Client;

using Testcontainers.RabbitMq;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The dashboard relay (11.2) against a real broker: a published <c>work-order.*</c> event on
/// <c>artifice.events</c> reaches a browser over SignalR, through the API's own non-competing
/// <c>artifice.dashboard</c> queue. Runs a real Kestrel host so a real SignalR client can connect;
/// only RabbitMQ is containerised because the relay touches no database. Requires Docker.
/// <para>
/// These own three claims the story makes: that the feed <em>is</em> the same stream the pipeline
/// runs on, that the dashboard queue never steals a message from the worker's, and that a failed
/// broadcast still acks and never requeues — a dropped screen frame, not a lost unit of work.
/// </para>
/// </summary>
[Collection(BrokerTestCollection.Name)]
public class DashboardRelayTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.11").Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    [Fact]
    public async Task A_published_event_reaches_a_connected_SignalR_client_via_the_relay()
    {
        await using var app = await StartApiAsync();
        var received = new List<DashboardEvent>();
        await using var client = await ConnectClientAsync(app, received);

        // Pre-bind the dashboard queue (idempotent with the relay's own declare) so the direct
        // exchange can't drop the message in the window before the relay finishes binding — the
        // same guard the worker tests use.
        await PreBindDashboardQueueAsync(app);

        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        await PublishAsync(app,
            new WorkOrderFaulted(workOrderId, "PRD-RELAY", "cap exceeded", 3, DateTime.UtcNow),
            eventId, correlationId);

        var relayed = await Poll(() =>
        {
            lock (received) { return received.FirstOrDefault(e => e.EventId == eventId); }
        });

        Assert.NotNull(relayed);
        Assert.Equal("work-order.faulted", relayed!.EventType);
        Assert.Equal(workOrderId, relayed.WorkOrderId);
        Assert.Equal(correlationId, relayed.CorrelationId);
    }

    [Fact]
    public async Task The_dashboard_queue_does_not_steal_the_message_from_the_worker_queue()
    {
        await using var app = await StartApiAsync();
        var received = new List<DashboardEvent>();
        await using var client = await ConnectClientAsync(app, received);

        await PreBindDashboardQueueAsync(app);

        // A stand-in for artifice.workers: an independent durable queue bound to the same key. Two
        // queues on one exchange is fan-out done honestly — each gets its own copy.
        const string workerObserver = "test.worker.observer";
        await using (var setup = await ChannelAsync(app))
        {
            await setup.QueueDeclareAsync(workerObserver, durable: true, exclusive: false, autoDelete: false);
            await setup.QueueBindAsync(workerObserver, Exchange, WorkOrderEventTypes.Faulted);
        }

        var eventId = Guid.NewGuid();
        await PublishAsync(app,
            new WorkOrderFaulted(Guid.NewGuid(), "PRD-FANOUT", "cap exceeded", 3, DateTime.UtcNow),
            eventId, Guid.NewGuid());

        // The relay's queue delivered it to the SignalR client...
        var relayed = await Poll(() =>
        {
            lock (received) { return received.FirstOrDefault(e => e.EventId == eventId); }
        });
        Assert.NotNull(relayed);

        // ...and the independent worker-style queue still holds its own copy. The dashboard's
        // consumption did not remove it: both fired.
        var onWorkerQueue = await Poll(async () =>
        {
            await using var channel = await ChannelAsync(app);
            var result = await channel.BasicGetAsync(workerObserver, autoAck: true);
            return result is null ? null : "present";
        });
        Assert.Equal("present", onWorkerQueue);
    }

    [Fact]
    public async Task A_broadcast_failure_still_acks_and_does_not_requeue()
    {
        var broadcaster = new CountingBroadcaster(throwOnBroadcast: true);
        await using var app = await StartApiAsync(broadcaster);

        await PreBindDashboardQueueAsync(app);
        await PublishAsync(app,
            new WorkOrderFaulted(Guid.NewGuid(), "PRD-ACK", "cap exceeded", 3, DateTime.UtcNow),
            Guid.NewGuid(), Guid.NewGuid());

        // The relay tried to broadcast...
        var attempted = await Poll(() => broadcaster.Calls >= 1 ? "attempted" : null);
        Assert.Equal("attempted", attempted);

        // ...and having thrown, acked rather than requeued. A requeue would redeliver the same
        // message forever, so the call count would climb; instead it stays at one and the queue
        // drains to empty. A dropped frame, not a retried unit of work.
        await Task.Delay(1500);
        Assert.Equal(1, broadcaster.Calls);

        await using var channel = await ChannelAsync(app);
        var depth = await channel.MessageCountAsync(DashboardRelay.QueueName);
        Assert.Equal(0u, depth);
    }

    // ---- Test host + helpers ----

    /// <summary>
    /// A minimal API host: the relay, the hub and the shared connection, on a real Kestrel port and
    /// pointed at the container. Deliberately not the whole API — this proves the relay path alone.
    /// </summary>
    private async Task<WebApplication> StartApiAsync(IDashboardBroadcaster? broadcaster = null)
    {
        var amqp = new Uri(_rabbit.GetConnectionString());
        var userInfo = amqp.UserInfo.Split(':', 2);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMqConfiguration:Host"] = amqp.Host,
            ["RabbitMqConfiguration:Port"] = amqp.Port.ToString(),
            ["RabbitMqConfiguration:Username"] = Uri.UnescapeDataString(userInfo[0]),
            ["RabbitMqConfiguration:Password"] = Uri.UnescapeDataString(userInfo.Length > 1 ? userInfo[1] : string.Empty),
            ["RabbitMqConfiguration:VirtualHost"] = "/",
            ["RabbitMqConfiguration:ExchangeName"] = Exchange,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Just the connection (declares artifice.events on first use), the hub and the relay.
        builder.Services.AddRabbitMqConnection(builder.Configuration);
        builder.Services.AddSignalR();
        if (broadcaster is not null)
        {
            builder.Services.AddSingleton(broadcaster);
        }
        else
        {
            builder.Services.AddSingleton<IDashboardBroadcaster, HubDashboardBroadcaster>();
        }
        builder.Services.AddHostedService<DashboardRelay>();

        var app = builder.Build();
        app.MapHub<DashboardHub>(DashboardHub.Route);
        await app.StartAsync();
        return app;
    }

    private static async Task<HubConnection> ConnectClientAsync(WebApplication app, List<DashboardEvent> sink)
    {
        var baseUrl = app.Urls.First().TrimEnd('/');
        var connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}{DashboardHub.Route}")
            .Build();

        connection.On<DashboardEvent>("WorkOrderEvent", @event =>
        {
            lock (sink) { sink.Add(@event); }
        });

        await connection.StartAsync();
        return connection;
    }

    private static async Task PreBindDashboardQueueAsync(WebApplication app)
    {
        await using var channel = await ChannelAsync(app);
        await channel.QueueDeclareAsync(
            DashboardRelay.QueueName, durable: false, exclusive: false, autoDelete: true,
            arguments: DashboardRelay.QueueArguments);

        foreach (var routingKey in WorkOrderEventTypes.All)
        {
            await channel.QueueBindAsync(DashboardRelay.QueueName, Exchange, routingKey);
        }
    }

    private static async Task PublishAsync<T>(WebApplication app, T payload, Guid eventId, Guid correlationId)
        where T : IntegrationEvent
    {
        var envelope = new EventEnvelope<T>(
            eventId, payload.EventType, payload.SchemaVersion, correlationId, DateTime.UtcNow, payload);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);

        await using var channel = await ChannelAsync(app);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            MessageId = eventId.ToString(),
            CorrelationId = correlationId.ToString(),
            Type = payload.EventType,
        };

        await channel.BasicPublishAsync(
            exchange: Exchange, routingKey: payload.EventType, mandatory: false,
            basicProperties: properties, body: body);
    }

    private static async Task<IChannel> ChannelAsync(WebApplication app)
        => await app.Services.GetRequiredService<IRabbitMqConnection>().CreateChannelAsync();

    private static async Task<T?> Poll<T>(Func<T?> read, int seconds = 20) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var value = read();
            if (value is not null) return value;
            await Task.Delay(100);
        }
        return read();
    }

    private static async Task<T?> Poll<T>(Func<Task<T?>> read, int seconds = 20) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null) return value;
            await Task.Delay(100);
        }
        return await read();
    }

    private sealed class CountingBroadcaster : IDashboardBroadcaster
    {
        private readonly bool _throw;
        private int _calls;

        public CountingBroadcaster(bool throwOnBroadcast) => _throw = throwOnBroadcast;

        public int Calls => Volatile.Read(ref _calls);

        public Task BroadcastAsync(DashboardEvent @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (_throw) throw new InvalidOperationException("broadcast boom");
            return Task.CompletedTask;
        }
    }
}
