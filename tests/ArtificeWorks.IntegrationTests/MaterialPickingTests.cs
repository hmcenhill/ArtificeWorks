using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The reservation guarantees of Epic 5, proved against a real Postgres: the happy-path pick
/// (5.2), all-or-nothing reservation with an OnHold outcome and no double-allocation under
/// contention (5.3), and idempotent consumption (5.4).
/// </summary>
public class MaterialPickingTests : IClassFixture<MaterialPickingFixture>
{
    private readonly MaterialPickingFixture _fixture;

    public MaterialPickingTests(MaterialPickingFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------------------------------------------------------------- 5.2 happy path

    [Fact]
    public async Task Picking_draws_the_bom_reserves_and_announces_it()
    {
        var scenario = await Seed("HAPPY",
            orderQty: 3,
            ("CHASSIS", onHand: 10, qtyPerUnit: 1),
            ("BEARING", onHand: 50, qtyPerUnit: 6));

        var result = await Pick(scenario.WorkOrderId);

        Assert.Equal(PickOutcome.Picked, result.Outcome);

        // On-hand drops by QtyPerUnit × OrderItemQty for every line.
        Assert.Equal(7u, await OnHand(scenario.ComponentId("CHASSIS")));
        Assert.Equal(32u, await OnHand(scenario.ComponentId("BEARING")));

        // The pick is recorded as its own auditable row...
        await using var context = _fixture.NewContext();
        var reservation = await context.MaterialReservations
            .Include(r => r.Lines)
            .SingleAsync(r => r.WorkOrderId == scenario.WorkOrderId);
        Assert.Equal(2, reservation.Lines.Count);
        Assert.Equal(18u, reservation.Lines.Single(l => l.ComponentId == scenario.ComponentId("BEARING")).Quantity);

        // ...and visibly in the work order's state history.
        var history = await History(scenario.WorkOrderId);
        var pickNote = Assert.Single(history, h => (h.Notes ?? "").Contains("Materials picked"));
        Assert.Equal(WorkOrderStatus.Scheduled, pickNote.Status);

        // Picking does NOT start production — Epic 6 owns Scheduled → InProcess. It hands
        // over by publishing instead.
        Assert.Equal(WorkOrderStatus.Scheduled, await Status(scenario.WorkOrderId));

        var reserved = Assert.Single(
            _fixture.Published.OfType<MaterialsReserved>().Where(e => e.WorkOrderId == scenario.WorkOrderId));
        Assert.Equal(3u, reserved.Quantity);
        Assert.Equal(2, reserved.Lines.Count);
    }

    // ------------------------------------------------- 5.3 all-or-nothing / insufficient

    [Fact]
    public async Task Insufficient_stock_holds_the_order_and_reserves_nothing()
    {
        // The first line is plentiful, the second is one short: the classic partial BOM.
        var scenario = await Seed("SHORT",
            orderQty: 2,
            ("CHASSIS", onHand: 10, qtyPerUnit: 1),
            ("CORE", onHand: 1, qtyPerUnit: 1));

        var result = await Pick(scenario.WorkOrderId);

        Assert.Equal(PickOutcome.InsufficientStock, result.Outcome);

        // All-or-nothing: the plentiful line is untouched, not half-reserved.
        Assert.Equal(10u, await OnHand(scenario.ComponentId("CHASSIS")));
        Assert.Equal(1u, await OnHand(scenario.ComponentId("CORE")));

        await using var context = _fixture.NewContext();
        Assert.False(await context.MaterialReservations.AnyAsync(r => r.WorkOrderId == scenario.WorkOrderId));

        // Visible: OnHold, with the short component named in the state history.
        Assert.Equal(WorkOrderStatus.OnHold, await Status(scenario.WorkOrderId));
        var holdNote = Assert.Single(await History(scenario.WorkOrderId), h => (h.Notes ?? "").Contains("Insufficient stock"));
        Assert.Contains(scenario.ComponentId("CORE"), holdNote.Notes);

        // Nothing was reserved, so nothing was announced to the next stage.
        Assert.Empty(_fixture.Published.OfType<MaterialsReserved>().Where(e => e.WorkOrderId == scenario.WorkOrderId));
    }

    // ------------------------------------------------------------- 5.3 no double-allocation

    [Fact]
    public async Task Concurrent_orders_never_reserve_the_same_stock_twice()
    {
        // Deliberately scarce: 10 orders of 1 unit each, but stock for only 4.
        const int orders = 10;
        const uint stock = 4;

        var scenario = await Seed("RACE", orderQty: 1, orderCount: orders, ("CHASSIS", onHand: stock, qtyPerUnit: 1));

        // Actually race them — one scope (and so one DbContext and one transaction) each.
        var results = await Task.WhenAll(scenario.WorkOrderIds.Select(id => Task.Run(() => Pick(id))));

        var picked = results.Count(r => r.Outcome == PickOutcome.Picked);
        var held = results.Count(r => r.Outcome == PickOutcome.InsufficientStock);

        Assert.Equal((int)stock, picked);
        Assert.Equal(orders - (int)stock, held);

        // The invariant: total reserved never exceeds what was on the shelf.
        await using var context = _fixture.NewContext();
        var reservedTotal = await context.MaterialReservationLines
            .Where(line => line.ComponentId == scenario.ComponentId("CHASSIS"))
            .SumAsync(line => (long)line.Quantity);

        Assert.Equal(stock, (uint)reservedTotal);
        Assert.Equal(0u, await OnHand(scenario.ComponentId("CHASSIS")));

        // And the losers are held, not silently dropped.
        var heldOrders = 0;
        foreach (var id in scenario.WorkOrderIds)
        {
            if (await Status(id) == WorkOrderStatus.OnHold)
            {
                heldOrders++;
            }
        }
        Assert.Equal(orders - (int)stock, heldOrders);
    }

    // ------------------------------------------------------------------ 5.4 idempotency

    [Fact]
    public async Task A_redelivered_event_picks_once()
    {
        var scenario = await Seed("DUPE", orderQty: 2, ("CHASSIS", onHand: 10, qtyPerUnit: 1));

        var first = await Pick(scenario.WorkOrderId);
        var second = await Pick(scenario.WorkOrderId);

        Assert.Equal(PickOutcome.Picked, first.Outcome);
        Assert.Equal(PickOutcome.AlreadyPicked, second.Outcome);

        // Decremented once...
        Assert.Equal(8u, await OnHand(scenario.ComponentId("CHASSIS")));

        await using var context = _fixture.NewContext();
        Assert.Equal(1, await context.MaterialReservations.CountAsync(r => r.WorkOrderId == scenario.WorkOrderId));

        // ...and no second pick note: a note per redelivery would itself be a non-idempotent
        // side effect. The skip is logged instead.
        Assert.Single(await History(scenario.WorkOrderId), h => (h.Notes ?? "").Contains("Materials picked"));

        // The duplicate is a handled outcome, so no second hand-off event either.
        Assert.Single(_fixture.Published.OfType<MaterialsReserved>().Where(e => e.WorkOrderId == scenario.WorkOrderId));
    }

    [Fact]
    public async Task Simultaneous_duplicate_deliveries_still_pick_once()
    {
        // The nastier case: two deliveries of the same event race past the "already picked?"
        // pre-check together. Only the unique index on the reservation's work order id can
        // catch this — the loser's decrements roll back with its failed insert.
        var scenario = await Seed("DUPE-RACE", orderQty: 2, ("CHASSIS", onHand: 10, qtyPerUnit: 1));

        var results = await Task.WhenAll(
            Task.Run(() => Pick(scenario.WorkOrderId)),
            Task.Run(() => Pick(scenario.WorkOrderId)));

        Assert.Equal(1, results.Count(r => r.Outcome == PickOutcome.Picked));
        Assert.Equal(1, results.Count(r => r.Outcome == PickOutcome.AlreadyPicked));
        Assert.Equal(8u, await OnHand(scenario.ComponentId("CHASSIS")));

        await using var context = _fixture.NewContext();
        Assert.Equal(1, await context.MaterialReservations.CountAsync(r => r.WorkOrderId == scenario.WorkOrderId));
    }

    // ------------------------------------------------------------------- 5.1 seeded catalog

    [Fact]
    public async Task The_seeded_product_lines_really_do_share_seventy_percent_of_their_parts()
    {
        await using var context = _fixture.NewContext();
        await CatalogSeeder.SeedAsync(context);

        var seededProductIds = CatalogSeeder.SeededProductIds;
        var products = await context.Products
            .AsNoTracking()
            .Include(p => p.BillOfMaterials)
                .ThenInclude(line => line.Component)
            .Where(p => seededProductIds.Contains(p.ItemId))
            .ToListAsync();

        Assert.Equal(3, products.Count);

        var boms = products.ToDictionary(
            p => p.ItemId,
            p => p.BillOfMaterials.Select(line => line.Component.ComponentId).ToHashSet());

        // The shared platform is in every line's BOM...
        var shared = boms.Values.Skip(1).Aggregate(
            new HashSet<string>(boms.Values.First()),
            (common, bom) => { common.IntersectWith(bom); return common; });

        Assert.Equal(CatalogSeeder.SharedPlatformComponentIds.Order(), shared.Order());

        // ...and it is 70% of each of them — the pitch, made literally true in the data.
        foreach (var bom in boms.Values)
        {
            Assert.Equal(10, bom.Count);
            Assert.Equal(0.7, (double)shared.Count / bom.Count, precision: 2);
            Assert.NotEmpty(bom.Except(shared)); // and each line really does have its own trade parts
        }

        // Every seeded component carries stock, so a fresh factory can actually build.
        var seededComponentIds = boms.Values.SelectMany(bom => bom).Distinct().ToList();
        Assert.False(
            await context.Components.AnyAsync(c => seededComponentIds.Contains(c.ComponentId) && c.OnHand == 0),
            "seeded components must have on-hand inventory");

        // Re-running the seeder is a no-op, not a duplicate catalog.
        await CatalogSeeder.SeedAsync(context);
        Assert.Equal(10, await context.BomLines.CountAsync(line => line.ProductId == CatalogSeeder.SeededProductIds[0]));
    }

    // ------------------------------------------------------------------------- helpers

    private async Task<PickResult> Pick(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MaterialPickingService>();
        return await service.PickMaterials(workOrderId);
    }

    private async Task<uint> OnHand(string componentId)
    {
        await using var context = _fixture.NewContext();
        return (await context.Components.AsNoTracking().SingleAsync(c => c.ComponentId == componentId)).OnHand;
    }

    private async Task<WorkOrderStatus> Status(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return (await context.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrderId)).CurrentStatus;
    }

    private async Task<List<WorkOrderStateHistory>> History(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.OrderStateHistory
            .AsNoTracking()
            .Where(h => h.WorkOrderId == workOrderId)
            .OrderBy(h => h.ChangedUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Builds an isolated scenario — its own product, components and scheduled work orders,
    /// name-spaced by <paramref name="tag"/> so tests in this class never contend for stock
    /// with each other.
    /// </summary>
    private async Task<Scenario> Seed(
        string tag,
        uint orderQty,
        params (string Name, uint OnHand, uint QtyPerUnit)[] lines)
        => await Seed(tag, orderQty, orderCount: 1, lines);

    private async Task<Scenario> Seed(
        string tag,
        uint orderQty,
        int orderCount,
        params (string Name, uint OnHand, uint QtyPerUnit)[] lines)
    {
        await using var context = _fixture.NewContext();

        var product = new Product($"PRD-{tag}", $"{tag} Automaton");
        context.Products.Add(product);

        foreach (var (name, onHand, qtyPerUnit) in lines)
        {
            var component = new Component($"CMP-{tag}-{name}", name, onHand);
            context.Components.Add(component);
            product.AddBomLine(component, qtyPerUnit);
        }

        var workOrderIds = new List<Guid>();
        for (var i = 0; i < orderCount; i++)
        {
            var workOrder = new WorkOrder("seed", product, orderQty);
            workOrder.AdvanceToNextStep("seed"); // Intake -> Scheduled
            context.WorkOrders.Add(workOrder);
            workOrderIds.Add(workOrder.Id);
        }

        await context.SaveChangesAsync();
        return new Scenario(tag, workOrderIds);
    }

    private sealed record Scenario(string Tag, IReadOnlyList<Guid> WorkOrderIds)
    {
        public Guid WorkOrderId => WorkOrderIds[0];
        public string ComponentId(string name) => $"CMP-{Tag}-{name}";
    }
}
