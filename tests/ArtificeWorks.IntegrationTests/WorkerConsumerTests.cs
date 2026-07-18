using ArtificeWorks.Application.Interfaces;
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

using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// End-to-end proof of the async backbone against real brokers (the publish→consume test
/// deferred from 4.1): the real RabbitMQ publisher emits <see cref="WorkOrderScheduled"/>,
/// the hosted worker consumes it, and its handler appends a state-history note observable
/// in Postgres. Requires Docker (Testcontainers RabbitMQ + Postgres).
/// </summary>
public class WorkerConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder().Build();
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
            ["RabbitMqConfiguration:ExchangeName"] = "artifice.events",
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();

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
    public async Task PublishedScheduledEvent_IsConsumedAndHandlerAppendsHistoryNote()
    {
        // Arrange — a work order already advanced to Scheduled, persisted.
        var product = new Product("Item-Worker-001", "Worker Test Product");
        Guid workOrderId;
        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var workOrder = new WorkOrder("seed", product, 2);
            workOrder.AdvanceToNextStep("seed"); // Intake -> Scheduled
            context.Products.Add(product);
            context.WorkOrders.Add(workOrder);
            await context.SaveChangesAsync();
            workOrderId = workOrder.Id;
        }

        var routingKey = new WorkOrderScheduled(Guid.Empty, string.Empty, string.Empty, 0, default).EventType;

        // Declare + bind the queue ourselves before publishing (idempotent with the
        // consumer's own declare) so the message can't be dropped by the direct exchange
        // if the consumer hasn't finished binding yet — makes the test deterministic.
        using (var scope = _host.Services.CreateScope())
        {
            var connection = scope.ServiceProvider.GetRequiredService<IRabbitMqConnection>();
            await using var channel = await connection.CreateChannelAsync();
            await channel.QueueDeclareAsync(
                RabbitMqConsumerService.QueueName, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(RabbitMqConsumerService.QueueName, "artifice.events", routingKey);
        }

        // Act — publish the real event through the real publisher.
        using (var scope = _host.Services.CreateScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            await publisher.PublishAsync(new WorkOrderScheduled(
                workOrderId, product.ItemId, product.ItemName, 2, DateTime.UtcNow));
        }

        // Assert — poll until the worker's note lands in state history. (Filter on Notes,
        // not CompletedBy: the latter is currently unmapped by EF and never persisted.)
        var history = new List<WorkOrderStateHistory>();
        WorkOrderStateHistory? workerNote = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            history = await context.OrderStateHistory
                .Where(h => h.WorkOrderId == workOrderId)
                .OrderBy(h => h.ChangedUtc)
                .ToListAsync();

            workerNote = history.LastOrDefault(h =>
                (h.Notes ?? string.Empty).Contains("worker", StringComparison.OrdinalIgnoreCase));
            if (workerNote is not null)
            {
                break;
            }
            await Task.Delay(250);
        }

        Assert.NotNull(workerNote);
        Assert.Equal(WorkOrderStatus.Scheduled, workerNote!.Status);
        // Intake + Scheduled (seeded) + the worker's acknowledgement note.
        Assert.Equal(3, history.Count);
    }
}
