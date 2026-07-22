using System.Diagnostics;
using System.Text.Json;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Shipping;
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

using RabbitMQ.Client;

using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The whole async pipeline against real infrastructure. The API-side publisher emits
/// <see cref="WorkOrderScheduled"/>; from there the worker alone carries the order through
/// picking, production, inspection, shipping and dispatch to <strong>Completed</strong>, each
/// stage triggered by an event the previous one published. Requires Docker (Testcontainers
/// RabbitMQ + Postgres).
/// <para>
/// This test owns the <em>plumbing</em> claim — that the pipeline really is driven by messages
/// over a broker rather than by method calls. The workflow guarantees (all-or-nothing
/// reservation, build-once, inspect-once) are proved in <see cref="MaterialPickingTests"/> and
/// <see cref="ProductionInspectionTests"/>, where they can actually be raced: with prefetch 1
/// on a single consumer, the broker path serializes deliveries and cannot demonstrate them.
/// </para>
/// </summary>
public class WorkerConsumerTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";
    private const string ObserverQueue = "test.materials-reserved.observer";
    private const string PassedObserverQueue = "test.inspection-passed.observer";
    private const string CompletedObserverQueue = "test.completed.observer";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.11").Build();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        // The RabbitMq module hands back an amqp://user:pass@host:port URI; project it onto
        // the RabbitMqConfiguration shape so we don't depend on the module's default creds.
        var amqp = new Uri(_rabbit.GetConnectionString());
        var userInfo = amqp.UserInfo.Split(':', 2);

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ArtificeWorksDatabase"] = _postgres.GetConnectionString(),
            ["RabbitMqConfiguration:Host"] = amqp.Host,
            ["RabbitMqConfiguration:Port"] = amqp.Port.ToString(),
            ["RabbitMqConfiguration:Username"] = Uri.UnescapeDataString(userInfo[0]),
            ["RabbitMqConfiguration:Password"] = Uri.UnescapeDataString(userInfo.Length > 1 ? userInfo[1] : string.Empty),
            ["RabbitMqConfiguration:VirtualHost"] = "/",
            ["RabbitMqConfiguration:ExchangeName"] = Exchange,
            // The shipped 1s poll would add a second per hop to a six-hop pipeline. The dispatcher
            // is the same code either way; only the patience differs.
            ["Outbox:PollIntervalMs"] = "100",
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);

        // Telemetry, registered exactly as the worker host registers it (9.1). No OTLP endpoint is
        // configured, so nothing leaves the process — but the ActivitySource, the meter and the
        // metrics recorder every service now depends on are all real.
        builder.AddArtificeWorksTelemetry(ArtificeWorksTelemetry.WorkerServiceName);

        builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        builder.Services.AddScoped<IProductRepository, ProductRepository>();
        builder.Services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        builder.Services.AddScoped<MaterialPickingService>();

        // Epic 6's workflow, registered exactly as the worker host registers it — including
        // the shipped defaults, so this test exercises the configuration that actually ships
        // (FailureRate 0.0: the factory runs unattended).
        builder.Services.AddProductionAndInspection(builder.Configuration);

        // Epic 7's workflow, likewise with the shipped defaults (RefusalRate 0.0, AutoBook true)
        // so the unattended factory this test claims to demonstrate is the one that ships.
        builder.Services.AddShipping(builder.Configuration);

        // Full messaging (connection + publisher) so this test drives the REAL publish path,
        // plus the consumption plumbing and every handler in the pipeline.
        builder.Services.AddRabbitMqMessaging(builder.Configuration);

        // Since 8.1 publishing is a two-step: handlers write outbox rows, this drains them. Without
        // it the pipeline is inert — which is itself worth knowing, and is why the dispatcher is
        // registered in both real hosts.
        builder.Services.AddOutboxDispatcher();

        builder.Services.AddEventConsumer();
        builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();
        builder.Services.AddEventHandler<MaterialsReserved, MaterialsReservedHandler>();
        builder.Services.AddEventHandler<ProductionCompleted, ProductionCompletedHandler>();
        builder.Services.AddEventHandler<ReworkRequired, ReworkRequiredHandler>();
        builder.Services.AddEventHandler<InspectionPassed, InspectionPassedHandler>();
        builder.Services.AddEventHandler<ShipmentScheduled, ShipmentScheduledHandler>();

        _host = builder.Build();

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            await context.Database.MigrateAsync();
        }

        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Scheduling_an_order_carries_it_all_the_way_to_completed_over_the_bus()
    {
        // Arrange — a product with a two-line BOM, stocked, and an order already Scheduled.
        var product = new Product("PRD-E2E", "End-to-end Automaton");
        var chassis = new Component("CMP-E2E-CHASSIS", "Chassis", onHand: 10);
        var bearing = new Component("CMP-E2E-BEARING", "Bearing Set", onHand: 60);
        product.AddBomLine(chassis, qtyPerUnit: 1);
        product.AddBomLine(bearing, qtyPerUnit: 6);

        Guid workOrderId;
        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var workOrder = new WorkOrder("seed", product, 2);
            workOrder.AdvanceToNextStep("seed"); // Intake -> Scheduled
            context.Products.Add(product);
            context.Components.AddRange(chassis, bearing);
            context.WorkOrders.Add(workOrder);
            await context.SaveChangesAsync();
            workOrderId = workOrder.Id;
        }

        var scheduledKey = RoutingKeyOf(new WorkOrderScheduled(Guid.Empty, "", "", 0, default));
        var reservedKey = RoutingKeyOf(new MaterialsReserved(Guid.Empty, "", 0, [], default));
        var passedKey = RoutingKeyOf(new InspectionPassed(Guid.Empty, "", [], default));
        var completedKey = RoutingKeyOf(new WorkOrderCompleted(Guid.Empty, "", "", "", [], default));

        // Declare + bind the worker's queue ourselves before publishing (idempotent with the
        // consumer's own declare) so the message can't be dropped by the direct exchange if
        // the consumer hasn't finished binding yet — makes the test deterministic. The
        // observer queues are ours: they watch events the worker publishes on the way through.
        using (var scope = _host.Services.CreateScope())
        {
            var connection = scope.ServiceProvider.GetRequiredService<IRabbitMqConnection>();
            await using var channel = await connection.CreateChannelAsync();
            await channel.QueueDeclareAsync(
                RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, scheduledKey);

            await channel.QueueDeclareAsync(ObserverQueue, durable: false, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(ObserverQueue, Exchange, reservedKey);

            await channel.QueueDeclareAsync(PassedObserverQueue, durable: false, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(PassedObserverQueue, Exchange, passedKey);

            await channel.QueueDeclareAsync(CompletedObserverQueue, durable: false, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(CompletedObserverQueue, Exchange, completedKey);
        }

        // Act — publish the real scheduling event through the real publisher, which since 8.1
        // means staging an outbox row and committing it. Nothing reaches the broker until the
        // dispatcher picks it up, exactly as in production.
        var correlationId = Guid.NewGuid();
        using (var scope = _host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = correlationId;
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            await publisher.PublishAsync(new WorkOrderScheduled(
                workOrderId, product.ItemId, product.ItemName, 2, DateTime.UtcNow));

            await scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>().SaveChangesAsync();
        }

        // Assert — the pick landed in Postgres...
        var reservation = await Poll(async () =>
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            return await context.MaterialReservations
                .Include(r => r.Lines)
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkOrderId == workOrderId);
        });

        Assert.NotNull(reservation);
        Assert.Equal(2, reservation!.Lines.Count);
        Assert.Equal(12u, reservation.Lines.Single(l => l.ComponentId == bearing.ComponentId).Quantity);

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var components = await context.Components.AsNoTracking()
                .Where(c => c.ComponentId.StartsWith("CMP-E2E-"))
                .ToDictionaryAsync(c => c.ComponentId);

            Assert.Equal(8u, components[chassis.ComponentId].OnHand);
            Assert.Equal(48u, components[bearing.ComponentId].OnHand);

            var history = await context.OrderStateHistory.AsNoTracking()
                .Where(h => h.WorkOrderId == workOrderId)
                .ToListAsync();
            Assert.Single(history, h => (h.Notes ?? "").Contains("Materials picked"));
        }

        // ...and the hand-off to production went back onto the bus, under the same
        // correlation id the original request carried.
        var announced = await Poll(() => ReadEvent<MaterialsReserved>(ObserverQueue));

        Assert.NotNull(announced);
        Assert.Equal(workOrderId, announced!.Payload.WorkOrderId);
        Assert.Equal(correlationId, announced.CorrelationId);
        Assert.Equal(2, announced.Payload.Lines.Count);

        // Nobody touched the API again: production, inspection, shipping and dispatch were each
        // triggered by the previous stage's event. The order should reach Completed on its own.
        var completed = await Poll(async () =>
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var order = await context.WorkOrders.AsNoTracking().SingleAsync(wo => wo.Id == workOrderId);
            return order.CurrentStatus == WorkOrderStatus.Completed ? order : null;
        });

        Assert.NotNull(completed);
        Assert.Equal(1, completed!.BuildAttempt);

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

            // Both ordered units exist as serialized rows owned by the order, and both passed.
            var units = await context.StockKeepingUnits.AsNoTracking()
                .Where(unit => EF.Property<Guid>(unit, "work_order_id") == workOrderId)
                .ToListAsync();

            Assert.Equal(2, units.Count);
            Assert.All(units, unit => Assert.Equal(UnitStatus.Passed, unit.Status));

            // One production attempt, one inspection — the dedupe keys, written for real.
            Assert.Equal(1, await context.ProductionRuns.CountAsync(run => run.WorkOrderId == workOrderId));
            Assert.Equal(1, await context.InspectionRuns.CountAsync(run => run.WorkOrderId == workOrderId));

            var history = await context.OrderStateHistory.AsNoTracking()
                .Where(entry => entry.WorkOrderId == workOrderId)
                .ToListAsync();
            Assert.Single(history, entry => (entry.Notes ?? "").Contains("Production started"));
            Assert.Single(history, entry => (entry.Notes ?? "").Contains("passed inspection"));
            Assert.Single(history, entry => (entry.Notes ?? "").Contains("Shipment booked"));
            Assert.Single(history, entry => (entry.Notes ?? "").Contains("Shipment dispatched"));

            // Exactly one parcel, dispatched, holding exactly the two units that passed.
            var shipment = await context.Shipments.AsNoTracking()
                .Include(s => s.Lines)
                .SingleAsync(s => s.WorkOrderId == workOrderId);

            Assert.Equal(ShipmentStatus.Dispatched, shipment.Status);
            Assert.NotNull(shipment.DispatchedUtc);
            Assert.Equal(2, shipment.Lines.Count);
            Assert.False(string.IsNullOrWhiteSpace(shipment.TrackingNumber));
        }

        // The middle of the pipeline, still under the correlation id the original HTTP request
        // started with, four stages later.
        var passed = await Poll(() => ReadEvent<InspectionPassed>(PassedObserverQueue));

        Assert.NotNull(passed);
        Assert.Equal(workOrderId, passed!.Payload.WorkOrderId);
        Assert.Equal(correlationId, passed.CorrelationId);
        Assert.Equal(2, passed.Payload.SerialNumbers.Count);

        // And the far end: the terminal announcement, six stages and one correlation id from
        // the single HTTP call that started it.
        var finished = await Poll(() => ReadEvent<WorkOrderCompleted>(CompletedObserverQueue));

        Assert.NotNull(finished);
        Assert.Equal(workOrderId, finished!.Payload.WorkOrderId);
        Assert.Equal(correlationId, finished.CorrelationId);
        Assert.Equal(2, finished.Payload.SerialNumbers.Count);
        Assert.False(string.IsNullOrWhiteSpace(finished.Payload.Carrier));
    }

    /// <summary>
    /// 9.1's headline acceptance criterion, over a real broker: <strong>one work order, one
    /// trace</strong>. The producer span the outbox dispatcher opens and the consumer span the
    /// worker opens must share a trace id and be parent and child — not two neatly instrumented,
    /// completely unrelated traces, which is what a default integration produces here.
    /// </summary>
    [Fact]
    public async Task A_work_order_crossing_the_broker_stays_in_one_trace()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ArtificeWorksTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity); } }
        };
        ActivitySource.AddActivityListener(listener);

        var product = new Product("PRD-TRACE-E2E", "Traced Automaton");
        var chassis = new Component("CMP-TRACE-CHASSIS", "Chassis", onHand: 10);
        product.AddBomLine(chassis, qtyPerUnit: 1);

        Guid workOrderId;
        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var workOrder = new WorkOrder("seed", product, 1);
            workOrder.AdvanceToNextStep("seed");
            context.Products.Add(product);
            context.Components.Add(chassis);
            context.WorkOrders.Add(workOrder);
            await context.SaveChangesAsync();
            workOrderId = workOrder.Id;
        }

        // The "request": one activity, inside which the event is staged and committed. Everything
        // downstream — dispatcher, broker, handler, the next stage's publish — has to end up under
        // this trace id or the outbox has broken the chain.
        string traceId;
        using (var request = ArtificeWorksTelemetry.ActivitySource.StartActivity("test request"))
        {
            Assert.NotNull(request);
            traceId = request!.TraceId.ToString();

            using var scope = _host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = Guid.NewGuid();
            await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(
                new WorkOrderScheduled(workOrderId, product.ItemId, product.ItemName, 1, DateTime.UtcNow));
            await scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>().SaveChangesAsync();
        }

        var completed = await Poll(async () =>
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var order = await context.WorkOrders.AsNoTracking().SingleAsync(wo => wo.Id == workOrderId);
            return order.CurrentStatus == WorkOrderStatus.Completed ? order : null;
        });
        Assert.NotNull(completed);

        List<Activity> captured;
        lock (spans) { captured = [.. spans]; }

        var ours = captured.Where(span => span.TraceId.ToString() == traceId).ToList();

        // At least one producer and one consumer span, all under the one trace the request opened.
        Assert.Contains(ours, span => span.Kind == ActivityKind.Producer);
        Assert.Contains(ours, span => span.Kind == ActivityKind.Consumer);

        // The consumer span for the FIRST hop is a child of the producer span that published it —
        // the specific edge the outbox would otherwise have severed.
        var producer = ours.First(span => span.Kind == ActivityKind.Producer
            && span.OperationName.EndsWith("work-order.scheduled", StringComparison.Ordinal));
        var consumer = ours.First(span => span.Kind == ActivityKind.Consumer
            && span.OperationName.EndsWith("work-order.scheduled", StringComparison.Ordinal));

        Assert.Equal(producer.SpanId.ToString(), consumer.ParentSpanId.ToString());

        // Spans carry the ids that make a trace searchable by something a human knows.
        Assert.Contains(ours, span => span.GetTagItem(ArtificeWorksTelemetry.WorkOrderIdAttribute) is string id
            && id == workOrderId.ToString());
        Assert.All(ours.Where(span => span.Kind != ActivityKind.Internal), span =>
            Assert.NotNull(span.GetTagItem(ArtificeWorksTelemetry.CorrelationIdAttribute)));

        // Every later stage — picking, production, inspection, shipping, dispatch — joined it too,
        // rather than starting fresh at each commit.
        Assert.True(ours.Count(span => span.Kind == ActivityKind.Consumer) >= 5,
            $"expected the whole pipeline under one trace; saw {ours.Count(s => s.Kind == ActivityKind.Consumer)} consumer span(s)");
    }

    private async Task<EventEnvelope<T>?> ReadEvent<T>(string queue) where T : IntegrationEvent
    {
        using var scope = _host.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<IRabbitMqConnection>();
        await using var channel = await connection.CreateChannelAsync();

        var message = await channel.BasicGetAsync(queue, autoAck: true);
        return message is null
            ? null
            : JsonSerializer.Deserialize<EventEnvelope<T>>(
                message.Body.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string RoutingKeyOf(IntegrationEvent @event) => @event.EventType;

    /// <summary>Polls until the asynchronous pipeline has caught up, or gives up.</summary>
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
            await Task.Delay(250);
        }
        return null;
    }
}
