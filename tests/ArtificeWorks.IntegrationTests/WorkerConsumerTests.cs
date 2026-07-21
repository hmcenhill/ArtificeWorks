using System.Text.Json;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Persistence;
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
/// The whole async pipeline against real infrastructure: the API-side publisher emits
/// <see cref="WorkOrderScheduled"/>, the hosted worker consumes it, picks the product's BOM
/// out of inventory, and publishes <see cref="MaterialsReserved"/> back onto the bus for
/// Epic 6's production stage. Requires Docker (Testcontainers RabbitMQ + Postgres).
/// <para>
/// This test owns the <em>plumbing</em> claim — that a scheduled order triggers reservation
/// via an event and not an API call. The reservation guarantees themselves (all-or-nothing,
/// concurrency, idempotency) are proved in <see cref="MaterialPickingTests"/>, where they can
/// actually be raced.
/// </para>
/// </summary>
public class WorkerConsumerTests : IAsyncLifetime
{
    private const string Exchange = "artifice.events";
    private const string ObserverQueue = "test.materials-reserved.observer";

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
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        builder.Services.AddScoped<IProductRepository, ProductRepository>();
        builder.Services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        builder.Services.AddScoped<MaterialPickingService>();

        // Full messaging (connection + publisher) so this test drives the REAL publish path,
        // plus the consumption plumbing and the handler under test.
        builder.Services.AddRabbitMqMessaging(builder.Configuration);
        builder.Services.AddEventConsumer();
        builder.Services.AddEventHandler<WorkOrderScheduled, WorkOrderScheduledHandler>();

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
    public async Task Scheduling_an_order_makes_the_worker_pick_its_materials_and_announce_them()
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

        // Declare + bind the worker's queue ourselves before publishing (idempotent with the
        // consumer's own declare) so the message can't be dropped by the direct exchange if
        // the consumer hasn't finished binding yet — makes the test deterministic. The second
        // queue is ours: it observes the event the worker publishes on the way out.
        using (var scope = _host.Services.CreateScope())
        {
            var connection = scope.ServiceProvider.GetRequiredService<IRabbitMqConnection>();
            await using var channel = await connection.CreateChannelAsync();
            await channel.QueueDeclareAsync(
                RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, Exchange, scheduledKey);

            await channel.QueueDeclareAsync(ObserverQueue, durable: false, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(ObserverQueue, Exchange, reservedKey);
        }

        // Act — publish the real scheduling event through the real publisher.
        var correlationId = Guid.NewGuid();
        using (var scope = _host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = correlationId;
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            await publisher.PublishAsync(new WorkOrderScheduled(
                workOrderId, product.ItemId, product.ItemName, 2, DateTime.UtcNow));
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
        var announced = await Poll(ReadReservedEvent);

        Assert.NotNull(announced);
        Assert.Equal(workOrderId, announced!.Payload.WorkOrderId);
        Assert.Equal(correlationId, announced.CorrelationId);
        Assert.Equal(2, announced.Payload.Lines.Count);
    }

    private async Task<EventEnvelope<MaterialsReserved>?> ReadReservedEvent()
    {
        using var scope = _host.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<IRabbitMqConnection>();
        await using var channel = await connection.CreateChannelAsync();

        var message = await channel.BasicGetAsync(ObserverQueue, autoAck: true);
        return message is null
            ? null
            : JsonSerializer.Deserialize<EventEnvelope<MaterialsReserved>>(
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
