using System.Text.Json;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Messaging.Outbox;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The transactional outbox (8.1), proved against a real Postgres: the row commits with the work
/// or not at all, the dispatcher publishes in order and exactly once even with two of them
/// running, a broker outage delays events without losing them — and the work order's new
/// concurrency token rejects a concurrent update instead of silently merging it.
/// </summary>
public class OutboxTests : IClassFixture<OutboxFixture>
{
    private readonly OutboxFixture _fixture;

    public OutboxTests(OutboxFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------- the row commits with the work

    [Fact]
    public async Task An_event_and_the_work_that_caused_it_commit_together()
    {
        var product = await SeedProduct("OB-TOGETHER");

        Guid workOrderId;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

            var workOrder = new WorkOrder("tester", await Attach(context, product), 1);
            workOrderId = workOrder.Id;

            context.WorkOrders.Add(workOrder);
            await publisher.PublishAsync(new WorkOrderCreated(
                workOrder.Id, product.ItemId, product.ItemName, 1, "tester", workOrder.CreatedUtc));

            // One SaveChanges for both. Seen from another connection, neither exists yet.
            await using (var probe = _fixture.NewContext())
            {
                Assert.Empty(await OutboxFor(probe, workOrderId));
                Assert.Null(await probe.WorkOrders.SingleOrDefaultAsync(wo => wo.Id == workOrder.Id));
            }

            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.NewContext();
        Assert.NotNull(await verify.WorkOrders.SingleOrDefaultAsync(wo => wo.Id == workOrderId));
        Assert.Single(await OutboxFor(verify, workOrderId));
    }

    [Fact]
    public async Task A_commit_that_fails_leaves_no_outbox_row()
    {
        var product = await SeedProduct("OB-ROLLBACK");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

            // A work order pointing at a product that does not exist: the FK rejects the insert,
            // and the outbox row is in the same transaction, so it goes down with it. This is the
            // property the whole story exists for — an announcement of work that never happened
            // is exactly as bad as work that was never announced.
            var orphan = new Product("OB-MISSING", "Never Seeded");
            var workOrder = new WorkOrder("tester", orphan, 1);

            context.Entry(orphan).State = EntityState.Unchanged;
            context.WorkOrders.Add(workOrder);
            await publisher.PublishAsync(new WorkOrderCreated(
                workOrder.Id, orphan.ItemId, orphan.ItemName, 1, "tester", workOrder.CreatedUtc));

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using var verify = _fixture.NewContext();
        Assert.Empty(await verify.OutboxMessages.Where(m => m.EventType == "work-order.created"
            && m.Payload.Contains("OB-MISSING")).ToListAsync());

        Assert.NotNull(product); // the fixture seeded fine; the failure above was the arranged one
    }

    // ------------------------------------------------------------------- the dispatcher

    [Fact]
    public async Task The_dispatcher_publishes_unsent_rows_in_id_order_and_marks_them_sent()
    {
        var correlationId = Guid.NewGuid();
        await Enqueue(correlationId, "one", "two", "three");

        await _fixture.NewDispatcher().DispatchBatchAsync(CancellationToken.None);

        var published = _fixture.Broker.Published.Where(p => p.CorrelationId == correlationId).ToList();
        Assert.Equal(["one", "two", "three"], published.Select(p => p.RoutingKey));

        await using var verify = _fixture.NewContext();
        var rows = await verify.OutboxMessages
            .Where(m => m.CorrelationId == correlationId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        Assert.All(rows, row => Assert.NotNull(row.SentUtc));
        Assert.All(rows, row => Assert.Equal(1, row.Attempts));
    }

    [Fact]
    public async Task A_broker_outage_delays_the_event_but_never_loses_it()
    {
        var correlationId = Guid.NewGuid();
        await Enqueue(correlationId, "outage.subject");

        _fixture.Broker.IsDown = true;
        try
        {
            await _fixture.NewDispatcher().DispatchBatchAsync(CancellationToken.None);

            await using var duringOutage = _fixture.NewContext();
            var row = await duringOutage.OutboxMessages.SingleAsync(m => m.CorrelationId == correlationId);

            // Unsent, blamed, and scheduled to try again — the resilience the old swallow-and-log
            // was protecting, minus the part where the message disappeared.
            Assert.Null(row.SentUtc);
            Assert.Equal(1, row.Attempts);
            Assert.Contains("unreachable", row.LastError);
            Assert.NotNull(row.NextAttemptUtc);
        }
        finally
        {
            _fixture.Broker.IsDown = false;
        }

        await _fixture.NewDispatcher().DispatchBatchAsync(CancellationToken.None);

        Assert.Contains(_fixture.Broker.Published, p => p.CorrelationId == correlationId);

        await using var afterRecovery = _fixture.NewContext();
        Assert.NotNull((await afterRecovery.OutboxMessages.SingleAsync(m => m.CorrelationId == correlationId)).SentUtc);
    }

    [Fact]
    public async Task Two_dispatchers_racing_the_same_table_publish_each_row_exactly_once()
    {
        var correlationId = Guid.NewGuid();
        await Enqueue(correlationId, Enumerable.Range(0, 10).Select(i => $"race.{i}").ToArray());

        // SKIP LOCKED is the entire mechanism: whichever dispatcher gets the row first holds it
        // for the life of its transaction, and the other one steps over it rather than blocking.
        var first = _fixture.NewDispatcher();
        var second = _fixture.NewDispatcher();

        await Task.WhenAll(
            Task.Run(() => first.DispatchBatchAsync(CancellationToken.None)),
            Task.Run(() => second.DispatchBatchAsync(CancellationToken.None)));

        var published = _fixture.Broker.Published.Where(p => p.CorrelationId == correlationId).ToList();

        Assert.Equal(10, published.Count);
        Assert.Equal(10, published.Select(p => p.EventId).Distinct().Count());

        await using var verify = _fixture.NewContext();
        var rows = await verify.OutboxMessages.Where(m => m.CorrelationId == correlationId).ToListAsync();
        Assert.All(rows, row => Assert.NotNull(row.SentUtc));
    }

    [Fact]
    public async Task The_envelope_is_serialized_at_write_time_with_the_correlation_id_that_caused_it()
    {
        var product = await SeedProduct("OB-CORRELATION");
        var correlationId = Guid.NewGuid();

        Guid workOrderId;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = correlationId;

            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            var workOrder = new WorkOrder("tester", await Attach(context, product), 1);
            workOrderId = workOrder.Id;

            context.WorkOrders.Add(workOrder);
            await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(new WorkOrderCreated(
                workOrder.Id, product.ItemId, product.ItemName, 1, "tester", workOrder.CreatedUtc));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.NewContext();
        var row = Assert.Single(await OutboxFor(verify, workOrderId));

        Assert.Equal(correlationId, row.CorrelationId);

        // The whole envelope is in the payload, so the dispatcher — a background loop with no
        // request and no delivery behind it — never has to invent metadata it cannot know.
        var envelope = JsonSerializer.Deserialize<EventEnvelope<WorkOrderCreated>>(
            row.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(envelope);
        Assert.Equal(correlationId, envelope!.CorrelationId);
        Assert.Equal(workOrderId, envelope.Payload.WorkOrderId);
        Assert.Equal("work-order.created", envelope.EventType);
    }

    // ------------------------------------------------------------ the concurrency token

    [Fact]
    public async Task A_concurrent_update_to_one_work_order_is_rejected_not_silently_merged()
    {
        var product = await SeedProduct("OB-CONCURRENCY");

        Guid workOrderId;
        await using (var arrange = _fixture.NewContext())
        {
            var workOrder = new WorkOrder("tester", await Attach(arrange, product), 1);
            workOrderId = workOrder.Id;
            arrange.WorkOrders.Add(workOrder);
            await arrange.SaveChangesAsync();
        }

        // Two writers who both loaded the same order. Before 8.1 the second simply won and the
        // first's history entry vanished; now `xmin` has moved under it and it fails loudly.
        await using var firstScope = _fixture.Services.CreateAsyncScope();
        await using var secondScope = _fixture.Services.CreateAsyncScope();

        var firstRepo = firstScope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();
        var secondRepo = secondScope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();

        var firstCopy = await firstRepo.GetWithHistory(workOrderId);
        var secondCopy = await secondRepo.GetWithHistory(workOrderId);

        Assert.True(firstCopy!.AdvanceToNextStep("first").Success);
        Assert.True(secondCopy!.SetHold("second", "held in parallel").Success);

        await firstRepo.Update(firstCopy);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondRepo.Update(secondCopy));

        // The winner's transition stands, whole. The loser wrote nothing at all — and because a
        // concurrency conflict is classified as transient, a delivery that hits this gets replayed
        // by 8.2's ladder rather than dropped.
        await using var verify = _fixture.NewContext();
        var stored = await verify.WorkOrders.AsNoTracking().SingleAsync(wo => wo.Id == workOrderId);
        Assert.Equal(WorkOrderStatus.Scheduled, stored.CurrentStatus);
    }

    // -------------------------------------------------------------------------- helpers

    private async Task<Product> SeedProduct(string id)
    {
        var product = new Product(id, $"Automaton {id}");
        await using var context = _fixture.NewContext();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    /// <summary>Re-attaches a seeded product to another context without re-inserting it.</summary>
    private static Task<Product> Attach(ArtificeWorksDbContext context, Product product)
    {
        var tracked = context.Products.Local.SingleOrDefault(p => p.ItemId == product.ItemId);
        if (tracked is not null)
        {
            return Task.FromResult(tracked);
        }

        var copy = new Product(product.ItemId, product.ItemName);
        context.Entry(copy).State = EntityState.Unchanged;
        return Task.FromResult(copy);
    }

    /// <summary>Writes outbox rows directly, so a dispatcher test doesn't need a workflow to drive it.</summary>
    private async Task Enqueue(Guid correlationId, params string[] eventTypes)
    {
        await using var context = _fixture.NewContext();
        foreach (var eventType in eventTypes)
        {
            context.OutboxMessages.Add(new OutboxMessage(
                Guid.NewGuid(), eventType, correlationId, $$"""{"eventType":"{{eventType}}"}""", DateTime.UtcNow));
        }
        await context.SaveChangesAsync();
    }

    private static Task<List<OutboxMessage>> OutboxFor(ArtificeWorksDbContext context, Guid workOrderId) =>
        context.OutboxMessages
            .Where(m => m.Payload.Contains(workOrderId.ToString()))
            .OrderBy(m => m.Id)
            .ToListAsync();
}
