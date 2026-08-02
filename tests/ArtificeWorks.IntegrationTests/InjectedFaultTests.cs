using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Recovery;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Chaos;
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
/// Epic 12.2 over the real broker: a visitor arms one of the two <em>broker-facing</em> faults
/// against a single order, the picking stage fires it, and the recovery that follows is Epic 8's
/// actual machinery — not a scripted animation.
/// <list type="bullet">
///   <item><description><see cref="InjectedFaultKind.TransientOnce"/>: the pick throws once, the
///     message climbs the retry ladder, and — the fault now disarmed — the redelivery completes the
///     order. It must <strong>recover</strong>, not exhaust the ladder and park.</description></item>
///   <item><description><see cref="InjectedFaultKind.Poison"/>: the pick throws a poison, the message
///     parks straight into <c>dead_letters</c>, and a human replay drives the order to
///     completion.</description></item>
/// </list>
/// <para>
/// The whole pipeline is wired (picking → production → inspection → shipping → dispatch) so "recovers"
/// can be asserted as <c>Completed</c>, and <c>AddChaos()</c> plus the parked-queue drain are added on
/// top so the faults can fire and park. Requires Docker (Testcontainers RabbitMQ + Postgres).
/// </para>
/// </summary>
[Collection(BrokerTestCollection.Name)]
public class InjectedFaultTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.11").Build();
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
            // The three rungs in hundreds of milliseconds: a transient fault must climb one and come
            // back, and a test that measures "recovered" rather than "was slow" should not pay the
            // shipped 5s for it.
            ["Retry:DelaysMs:0"] = "200",
            ["Retry:DelaysMs:1"] = "400",
            ["Retry:DelaysMs:2"] = "800",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.WorkerServiceName);

        builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        builder.Services.AddScoped<IProductRepository, ProductRepository>();
        builder.Services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        builder.Services.AddScoped<MaterialPickingService>();

        // The registry is what makes this whole test possible: with it wired, the picking service
        // resolves it and consults for an armed broker fault before it draws stock (12.2).
        builder.Services.AddChaos();

        // The full pipeline, shipped defaults (FailureRate 0.0, RefusalRate 0.0), so an order that
        // survives its pick reaches Completed on its own.
        builder.Services.AddProductionAndInspection(builder.Configuration);
        builder.Services.AddShipping(builder.Configuration);

        // 13.3's sub-assembly loop. Registered here for the same reason everything above it is:
        // this host mirrors the worker's own registrations, and InspectionPassedHandler now asks
        // "is this order internal?" before it reaches for a carrier.
        builder.Services.AddSubAssemblies();

        builder.Services.AddRabbitMqMessaging(builder.Configuration);
        builder.Services.AddOutboxDispatcher();
        builder.Services.AddDeadLetters();

        builder.Services.AddEventConsumer();
        builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();
        builder.Services.AddEventHandler<MaterialsReserved, MaterialsReservedHandler>();
        builder.Services.AddEventHandler<ProductionCompleted, ProductionCompletedHandler>();
        builder.Services.AddEventHandler<ReworkRequired, ReworkRequiredHandler>();
        builder.Services.AddEventHandler<InspectionPassed, InspectionPassedHandler>();
        builder.Services.AddEventHandler<ShipmentScheduled, ShipmentScheduledHandler>();
        builder.Services.AddEventHandler<WorkOrderCompleted, WorkOrderCompletedHandler>();

        // The parked-queue drain turns a poison park into a dead_letters row (8.3) — the durable
        // evidence the poison test replays.
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
    /// Declares and binds the worker's queue for every handled key before any test publishes
    /// (idempotent with the consumer's own declare). A direct exchange drops a message with no
    /// matching binding, so this closes the startup race the other broker fixtures also close.
    /// </summary>
    private async Task EnsureTopologyAsync()
    {
        var dispatcher = _host.Services.GetRequiredService<EventDispatcher>();
        var connection = _host.Services.GetRequiredService<IRabbitMqConnection>();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(
            RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
        foreach (var eventType in dispatcher.HandledEventTypes)
        {
            await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, eventType);
        }

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
    public async Task An_armed_transient_fault_fails_the_pick_once_then_the_order_recovers_to_completion()
    {
        var (workOrderId, product) = await SeedScheduledOrder("INJ-TRANSIENT");

        await Arm(workOrderId, InjectedFaultKind.TransientOnce);
        await Schedule(workOrderId, product);

        // The whole claim: the order does not exhaust the ladder and park — it reaches Completed,
        // which is only reachable by recovering on a later delivery once the fault is disarmed.
        var completed = await Poll(async () =>
        {
            await using var context = NewContext();
            var order = await context.WorkOrders.AsNoTracking().SingleAsync(o => o.Id == workOrderId);
            return order.CurrentStatus == WorkOrderStatus.Completed ? order : null;
        });

        Assert.NotNull(completed);

        await using var verify = NewContext();

        // It recovered, so it never parked: no dead letter exists for this order.
        Assert.False(await verify.DeadLetters.AsNoTracking().AnyAsync(d => d.WorkOrderId == workOrderId));

        // The pick happened exactly once (the recovery, not a re-fire), and so did production.
        Assert.Equal(1, await verify.MaterialReservations.CountAsync(r => r.WorkOrderId == workOrderId));
        Assert.Equal(1, await verify.ProductionRuns.CountAsync(r => r.WorkOrderId == workOrderId));

        // The fault fired exactly once and is now spent — a redelivery cannot re-trigger it.
        var faults = await verify.InjectedFaults.AsNoTracking()
            .Where(f => f.WorkOrderId == workOrderId).ToListAsync();
        var fault = Assert.Single(faults);
        Assert.Equal(InjectedFaultKind.TransientOnce, fault.Kind);
        Assert.NotNull(fault.FiredUtc);
    }

    [Fact]
    public async Task An_armed_poison_fault_parks_as_a_dead_letter_and_a_replay_completes_the_order()
    {
        var (workOrderId, product) = await SeedScheduledOrder("INJ-POISON");

        await Arm(workOrderId, InjectedFaultKind.Poison);
        await Schedule(workOrderId, product);

        // A poison parks immediately — no ladder — and the drain records it against the order.
        var letter = await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.DeadLetters.AsNoTracking()
                .FirstOrDefaultAsync(d => d.WorkOrderId == workOrderId);
        });

        Assert.NotNull(letter);
        Assert.Equal("work-order.scheduled", letter!.EventType);
        Assert.Equal(1, letter.Attempts); // parked on the first delivery, not after climbing the ladder
        Assert.Contains("injected poison fault", letter.LastError);
        Assert.Null(letter.ReplayedUtc);

        // Nothing downstream happened: the order sits where it was, no stock drawn.
        await using (var stalled = NewContext())
        {
            var order = await stalled.WorkOrders.AsNoTracking().SingleAsync(o => o.Id == workOrderId);
            Assert.Equal(WorkOrderStatus.Scheduled, order.CurrentStatus);
            Assert.False(await stalled.MaterialReservations.AnyAsync(r => r.WorkOrderId == workOrderId));
        }

        // A human presses replay. The fault is already spent, so the replayed message runs clean.
        var result = await Replay(letter.Id);
        Assert.Equal(ReplayOutcome.Replayed, result.Outcome);

        var completed = await Poll(async () =>
        {
            await using var context = NewContext();
            var order = await context.WorkOrders.AsNoTracking().SingleAsync(o => o.Id == workOrderId);
            return order.CurrentStatus == WorkOrderStatus.Completed ? order : null;
        });

        Assert.NotNull(completed);

        await using var verify = NewContext();
        Assert.Equal(1, await verify.MaterialReservations.CountAsync(r => r.WorkOrderId == workOrderId));
        var fault = Assert.Single(await verify.InjectedFaults.AsNoTracking()
            .Where(f => f.WorkOrderId == workOrderId).ToListAsync());
        Assert.NotNull(fault.FiredUtc);
    }

    [Fact]
    public async Task A_fault_on_one_order_does_not_disturb_a_neighbour_on_the_same_queue()
    {
        // Two orders share the one work queue. Only the first is armed — to poison, so its fate is
        // unmistakable — and the second must sail through untouched. Bounded blast radius, proved.
        var (armedId, armedProduct) = await SeedScheduledOrder("INJ-ARMED");
        var (freeId, freeProduct) = await SeedScheduledOrder("INJ-FREE");

        await Arm(armedId, InjectedFaultKind.Poison);
        await Schedule(armedId, armedProduct);
        await Schedule(freeId, freeProduct);

        // The neighbour completes on its own...
        var completed = await Poll(async () =>
        {
            await using var context = NewContext();
            var order = await context.WorkOrders.AsNoTracking().SingleAsync(o => o.Id == freeId);
            return order.CurrentStatus == WorkOrderStatus.Completed ? order : null;
        });
        Assert.NotNull(completed);

        // ...and the armed order parked, alone.
        var letter = await Poll(async () =>
        {
            await using var context = NewContext();
            return await context.DeadLetters.AsNoTracking()
                .FirstOrDefaultAsync(d => d.WorkOrderId == armedId);
        });
        Assert.NotNull(letter);

        await using var verify = NewContext();
        Assert.False(await verify.DeadLetters.AsNoTracking().AnyAsync(d => d.WorkOrderId == freeId));
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

    private async Task Arm(Guid workOrderId, InjectedFaultKind kind)
    {
        using var scope = _host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ChaosService>().Arm(workOrderId, kind, "test");
        Assert.Equal(ChaosArmOutcome.Armed, result.Outcome);
    }

    private async Task Schedule(Guid workOrderId, Product product)
    {
        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = Guid.NewGuid();

        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(
            new WorkOrderScheduled(workOrderId, product.ItemId, product.ItemName, 1, 1, DateTime.UtcNow));

        await scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>().SaveChangesAsync();
    }

    private async Task<ReplayResult> Replay(Guid deadLetterId)
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeadLetterService>().Replay(deadLetterId, force: false);
    }

    private ArtificeWorksDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ArtificeWorksDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    private static async Task<T?> Poll<T>(Func<Task<T?>> read) where T : class
    {
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
