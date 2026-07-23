using System.Text.Json;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Recovery;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Workflow;
using ArtificeWorks.Workers.Consuming;
using ArtificeWorks.Workers.Handlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Recovery end to end (8.3): a message that always fails climbs the ladder, parks, is drained
/// into a <c>dead_letters</c> row a human can read — and a replay of that row drives the work to
/// completion.
/// <para>
/// The failure is injected where a real one would happen: the component the order needs is not in
/// the catalog at all, so picking throws rather than returning a business outcome. Seeding the
/// component afterwards is the "someone fixed it" that makes replay meaningful, and is exactly
/// the shape Epic 12's failure injection will take.
/// </para>
/// </summary>
[Collection(BrokerTestCollection.Name)]
public class DeadLetterTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.11").Build();
    private readonly ExplodingPickingRepository _picking = new();

    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        var amqp = new Uri(_rabbit.GetConnectionString());
        var userInfo = amqp.UserInfo.Split(':', 2);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ArtificeWorksDatabase"] = _postgres.GetConnectionString(),
            ["RabbitMqConfiguration:Host"] = amqp.Host,
            ["RabbitMqConfiguration:Port"] = amqp.Port.ToString(),
            ["RabbitMqConfiguration:Username"] = Uri.UnescapeDataString(userInfo[0]),
            ["RabbitMqConfiguration:Password"] = Uri.UnescapeDataString(userInfo.Length > 1 ? userInfo[1] : string.Empty),
            ["RabbitMqConfiguration:VirtualHost"] = "/",
            ["RabbitMqConfiguration:ExchangeName"] = Exchange,
            ["Outbox:PollIntervalMs"] = "100",
            // Same three rungs, measured in hundreds of milliseconds.
            ["Retry:DelaysMs:0"] = "200",
            ["Retry:DelaysMs:1"] = "300",
            ["Retry:DelaysMs:2"] = "400",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Telemetry, registered exactly as the worker host registers it (9.1). No OTLP endpoint is
        // configured, so nothing leaves the process — but the ActivitySource, the meter and the
        // metrics recorder every service now depends on are all real.
        builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.WorkerServiceName);

        builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        builder.Services.AddScoped<IProductRepository, ProductRepository>();
        builder.Services.AddScoped<MaterialPickingService>();

        // The seam the fault is injected through: a reservation repository that refuses to work
        // until a test says otherwise. Everything downstream of it is the shipped code.
        builder.Services.AddSingleton(_picking);
        builder.Services.AddScoped<IMaterialReservationRepository>(sp =>
            new ExplodingReservationRepository(
                new MaterialReservationRepository(sp.GetRequiredService<ArtificeWorksDbContext>()),
                sp.GetRequiredService<ExplodingPickingRepository>()));

        builder.Services.AddRabbitMqMessaging(builder.Configuration);
        builder.Services.AddOutboxDispatcher();
        builder.Services.AddDeadLetters();

        builder.Services.AddEventConsumer();
        builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();
        builder.Services.AddHostedService<ParkedQueueDrain>();

        _host = builder.Build();

        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>().Database.MigrateAsync();
        }

        await _host.StartAsync();
        await EnsureTopologyAsync();
    }

    /// <summary>
    /// Declares and binds the worker's queue ourselves before any test publishes (idempotent with
    /// the consumer's own declare on startup).
    /// <para>
    /// Without this the suite is genuinely flaky, and instructively so: the outbox dispatcher and
    /// the consumer start concurrently, and a <em>direct</em> exchange silently drops a message
    /// with no matching binding. Under load the dispatcher wins that race and the event vanishes.
    /// Waiting here removes a startup race the test invented; it is not papering over one the
    /// system has, because in production both hosts have been running for some time before
    /// anybody presses anything.
    /// </para>
    /// </summary>
    private async Task EnsureTopologyAsync()
    {
        var connection = _host.Services.GetRequiredService<IRabbitMqConnection>();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(
            RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, "work-order.scheduled");

        await channel.QueueDeclareAsync(
            RetryConfiguration.ParkedQueueName, durable: true, exclusive: false, autoDelete: false);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task A_handler_that_always_fails_becomes_a_readable_row_and_a_replay_completes_the_work()
    {
        var (workOrderId, product) = await SeedScheduledOrder("DL-REPLAY");

        _picking.Explode = true;
        await Schedule(workOrderId, product);

        // Four deliveries later (one original, three rungs) the message parks, and the drain
        // turns it into a row.
        var letter = await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.DeadLetters.AsNoTracking()
                .FirstOrDefaultAsync(entry => entry.WorkOrderId == workOrderId);
        });

        Assert.NotNull(letter);

        // Everything needed to diagnose it without opening the RabbitMQ UI.
        Assert.Equal("work-order.scheduled", letter!.EventType);
        Assert.Equal(4, letter.Attempts);
        Assert.Contains("ladder is exhausted", letter.LastError);
        Assert.NotEqual(Guid.Empty, letter.CorrelationId);
        Assert.Null(letter.ReplayedUtc);

        // The payload survived verbatim, so the replay is the original message and not a
        // reconstruction of it.
        var envelope = JsonSerializer.Deserialize<EventEnvelope<WorkOrderScheduled>>(
            letter.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(workOrderId, envelope!.Payload.WorkOrderId);

        // Someone fixes the underlying problem...
        _picking.Explode = false;

        // ...and presses replay.
        var result = await Replay(letter.Id, force: false);
        Assert.Equal(ReplayOutcome.Replayed, result.Outcome);

        var reservation = await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.MaterialReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkOrderId == workOrderId);
        });

        Assert.NotNull(reservation);

        await using var verify = NewContext();
        var replayed = await verify.DeadLetters.AsNoTracking().SingleAsync(entry => entry.Id == letter.Id);
        Assert.NotNull(replayed.ReplayedUtc);
        Assert.Equal(1, replayed.ReplayCount);
    }

    [Fact]
    public async Task Replaying_twice_needs_force_and_still_does_not_duplicate_the_work()
    {
        var (workOrderId, product) = await SeedScheduledOrder("DL-TWICE");

        _picking.Explode = true;
        await Schedule(workOrderId, product);

        var letter = await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.DeadLetters.AsNoTracking()
                .FirstOrDefaultAsync(entry => entry.WorkOrderId == workOrderId);
        });
        Assert.NotNull(letter);

        _picking.Explode = false;

        Assert.Equal(ReplayOutcome.Replayed, (await Replay(letter!.Id, force: false)).Outcome);

        await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.MaterialReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkOrderId == workOrderId);
        });

        // A second click asks "did that work?"; the honest answer is a 409-shaped result, not
        // another silent re-send.
        var second = await Replay(letter.Id, force: false);
        Assert.Equal(ReplayOutcome.AlreadyReplayed, second.Outcome);

        // Forced through, it is harmless — the pick already happened, and 5.4's unique index on
        // material_reservations.work_order_id turns the replayed message into a skip. This is the
        // epic's whole bet: recovery is safe because duplication was already solved.
        Assert.Equal(ReplayOutcome.Replayed, (await Replay(letter.Id, force: true)).Outcome);

        await Task.Delay(2_000);

        await using var verify = NewContext();
        Assert.Equal(1, await verify.MaterialReservations.CountAsync(r => r.WorkOrderId == workOrderId));
        Assert.Equal(2, (await verify.DeadLetters.AsNoTracking().SingleAsync(e => e.Id == letter.Id)).ReplayCount);
    }

    [Fact]
    public async Task A_payload_that_cannot_be_parsed_becomes_a_readable_row_rather_than_an_exception()
    {
        // The drain's own poison case. If it threw here it would re-create the exact wedge 8.2
        // just fixed, one queue further along — with nothing behind it to catch the message.
        var wire = _host.Services.GetRequiredService<RabbitMqRawPublisher>();
        var correlationId = Guid.NewGuid();

        await wire.PublishToAsync(
            exchange: string.Empty,
            routingKey: RetryConfiguration.ParkedQueueName,
            payload: "this is not JSON at all {{{",
            eventId: Guid.NewGuid(),
            correlationId: correlationId,
            headers: new Dictionary<string, object?>
            {
                ["x-original-routing-key"] = "work-order.scheduled",
                ["x-death-reason"] = "the message is permanently unhandleable: arranged",
                [RabbitMqConsumerService.AttemptHeader] = 1,
            });

        var letter = await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.DeadLetters.AsNoTracking()
                .FirstOrDefaultAsync(entry => entry.CorrelationId == correlationId);
        });

        Assert.NotNull(letter);
        Assert.Equal("work-order.scheduled", letter!.EventType);
        Assert.Null(letter.WorkOrderId);
        Assert.Equal("this is not JSON at all {{{", letter.Payload);
        Assert.Contains("payload could not be parsed", letter.LastError);
    }

    // -------------------------------------------------------------------------- helpers

    private async Task<(Guid WorkOrderId, Product Product)> SeedScheduledOrder(string prefix)
    {
        var product = new Product($"PRD-{prefix}", $"{prefix} Automaton");
        var component = new Component($"CMP-{prefix}", "Frame", onHand: 50);
        product.AddBomLine(component, qtyPerUnit: 2);

        await using var context = NewContext();
        var workOrder = new WorkOrder("seed", product, 1);
        workOrder.AdvanceToNextStep("seed"); // Intake -> Scheduled

        context.Products.Add(product);
        context.Components.Add(component);
        context.WorkOrders.Add(workOrder);
        await context.SaveChangesAsync();

        return (workOrder.Id, product);
    }

    private async Task Schedule(Guid workOrderId, Product product)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = Guid.NewGuid();

        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(
            new WorkOrderScheduled(workOrderId, product.ItemId, product.ItemName, 1, DateTime.UtcNow));

        await scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>().SaveChangesAsync();
    }

    private async Task<ReplayResult> Replay(Guid deadLetterId, bool force)
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeadLetterService>().Replay(deadLetterId, force);
    }

    private ArtificeWorksDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ArtificeWorksDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    private static async Task<T?> Poll<T>(Func<Task<T?>> read) where T : class
    {
        // Generous, because this chain is four deliveries plus three TTLs plus an outbox poll plus
        // a drain, and the whole suite's containers compete for one machine.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null)
            {
                return value;
            }
            await Task.Delay(150);
        }
        return null;
    }
}

/// <summary>The switch a test flips to make the pipeline fail on demand.</summary>
public sealed class ExplodingPickingRepository
{
    public bool Explode { get; set; }
}

/// <summary>
/// A reservation repository that throws while the switch is on and delegates to the real one when
/// it isn't. An ordinary exception, so 8.2 classifies it as transient and the ladder runs — which
/// is the point: this is what a database blip looks like from the consumer loop.
/// </summary>
public sealed class ExplodingReservationRepository : IMaterialReservationRepository
{
    private readonly IMaterialReservationRepository _inner;
    private readonly ExplodingPickingRepository _switch;

    public ExplodingReservationRepository(IMaterialReservationRepository inner, ExplodingPickingRepository @switch)
    {
        _inner = inner;
        _switch = @switch;
    }

    public Task<MaterialReservation?> GetForWorkOrder(Guid workOrderId, CancellationToken cancellationToken = default)
        => _switch.Explode
            ? throw new InvalidOperationException("Arranged transient failure reading reservations.")
            : _inner.GetForWorkOrder(workOrderId, cancellationToken);

    public Task<ReservationCommitResult> TryReserve(
        Guid workOrderId,
        IReadOnlyList<ComponentDemand> demand,
        Func<MaterialReservation, Task>? stageWithReservation = null,
        CancellationToken cancellationToken = default)
        => _switch.Explode
            ? throw new InvalidOperationException("Arranged transient failure reserving materials.")
            : _inner.TryReserve(workOrderId, demand, stageWithReservation, cancellationToken);
}
