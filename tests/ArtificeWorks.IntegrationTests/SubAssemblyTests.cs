using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.SubAssemblies;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 13.3 against a real Postgres: the factory making what it hasn't got.
/// <para>
/// This is where the story actually lives. Every guarantee it makes is a database property — that a
/// redelivered scheduling event spawns <em>one</em> child (a filtered unique index), that a
/// completed child's stock and its terminal transition cannot come apart (one transaction), that
/// 10.4's sweep can never delete an order somebody is still building for (a <c>NOT EXISTS</c> and a
/// <c>NO ACTION</c> foreign key agreeing with each other). None of those can be shown in memory.
/// </para>
/// </summary>
public class SubAssemblyTests : IClassFixture<SubAssemblyFixture>
{
    private readonly SubAssemblyFixture _fixture;

    public SubAssemblyTests(SubAssemblyFixture fixture)
    {
        _fixture = fixture;
        _fixture.Verdicts.PassEverything();
    }

    // ------------------------------------------------------------------- the whole loop

    /// <summary>
    /// <strong>The epic's headline, end to end.</strong> An order short of a made component spawns
    /// exactly one child for the shortfall and holds naming what it waits for; the child runs the
    /// ordinary pipeline; on passing inspection it is stocked rather than shipped, so the shelf
    /// rises; its completion releases the parent, which re-picks and runs to Completed.
    /// <para>
    /// Nothing in this test drives a stage that 13.3 invented. Every call is a stage that existed
    /// before it, in the order a worker would make them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_order_short_of_a_made_component_makes_it_waits_for_it_and_finishes()
    {
        // 3 ordered, 1 control stack each; the shelf has 1. Two are missing.
        var world = await Seed("LOOP", orderQty: 3, madeOnHand: 1);

        var pick = await Pick(world.ParentId);

        Assert.Equal(PickOutcome.InsufficientStock, pick.Outcome);
        Assert.Contains("Waiting on sub-assembly", pick.Summary);
        Assert.Equal(WorkOrderStatus.OnHold, await Status(world.ParentId));

        // Exactly one child, for the shortfall — not for the whole demand, because the one already
        // on the shelf will be drawn by the re-pick.
        var child = Assert.Single(await ChildrenOf(world.ParentId));
        Assert.Equal(2u, child.OrderItemQty);
        Assert.Equal(world.MadeComponent, child.ForComponentId);
        Assert.Equal(world.MakerProductId, child.OrderedItem.ItemId);
        Assert.Equal(1, child.ParentAttemptNumber);
        Assert.Equal(1, child.TreeDepth);
        Assert.Equal(WorkOrderStatus.Scheduled, child.CurrentStatus);

        // Nothing was drawn: the reservation rolled back whole, including the parts that WERE there.
        Assert.Equal(1u, await OnHand(world.MadeComponent));
        Assert.Equal(world.BoughtSeed, await OnHand(world.BoughtComponent));

        // The child is an ordinary order. It picks, builds, and is inspected like anything else.
        Assert.Equal(PickOutcome.Picked, (await Pick(child.Id)).Outcome);
        Assert.Equal(ProductionOutcome.Built, (await Produce(child.Id, 1)).Outcome);
        Assert.Equal(InspectionOutcome.Passed, (await Inspect(child.Id, 1)).Outcome);
        Assert.Equal(WorkOrderStatus.Delivery, await Status(child.Id));

        // ...and then it diverges, exactly once: putaway rather than a carrier.
        var putaway = await PutAway(child.Id);

        Assert.Equal(PutawayOutcome.StockedAway, putaway.Outcome);
        Assert.Equal(2u, putaway.QuantityStocked);
        Assert.Equal(WorkOrderStatus.Completed, await Status(child.Id));
        Assert.Equal(3u, await OnHand(world.MadeComponent));   // 1 on the shelf + 2 just made
        Assert.Empty(await Shipments(child.Id));

        var completed = Assert.Single(PublishedFor<WorkOrderCompleted>(child.Id));
        Assert.Null(completed.Carrier);
        Assert.Null(completed.TrackingNumber);
        Assert.Equal(world.MadeComponent, completed.ForComponentId);

        // The completion releases the parent and asks it to pick again — same attempt, same demand.
        var release = await ReleaseParent(child.Id);

        Assert.Equal(ParentReleaseOutcome.Released, release.Outcome);
        Assert.Equal(WorkOrderStatus.Scheduled, await Status(world.ParentId));

        var rescheduled = Assert.Single(PublishedFor<WorkOrderScheduled>(world.ParentId));
        Assert.Equal(1, rescheduled.AttemptNumber);
        Assert.Equal(3u, rescheduled.Quantity);

        // And the parent runs to the end, having actually consumed the parts its child built.
        Assert.Equal(PickOutcome.Picked, (await Pick(world.ParentId, 1, 3)).Outcome);
        Assert.Equal(0u, await OnHand(world.MadeComponent));
        Assert.Equal(ProductionOutcome.Built, (await Produce(world.ParentId, 1)).Outcome);
        Assert.Equal(InspectionOutcome.Passed, (await Inspect(world.ParentId, 1)).Outcome);

        // The parent ships, because it is not a sub-assembly order.
        Assert.Equal(PutawayOutcome.NotASubAssembly, (await PutAway(world.ParentId)).Outcome);
    }

    // --------------------------------------------------------------------- idempotency

    /// <summary>
    /// <strong>The dedupe key, raced.</strong> Two deliveries of the same scheduling event, at once,
    /// both find the shelf short — and exactly one child exists afterwards. The pre-check cannot
    /// deliver that (both can pass it simultaneously); the filtered unique index on
    /// <c>(parent, attempt, component)</c> is what does.
    /// <para>
    /// <strong>The loser is allowed to throw, and that is 8.1 working rather than a gap.</strong>
    /// It loses on either the unique index or the parent's <c>xmin</c> token — whichever the
    /// database reaches first — and both are classified transient, so the message climbs a rung of
    /// 8.2's ladder and comes back. What must be true is that the redelivery is <em>clean</em>: it
    /// finds the open request in the pre-check, holds, and spawns nothing. That second half is
    /// asserted here too, because a race that resolves into a permanent stall would pass the first
    /// half on its own.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Simultaneous_deliveries_of_a_scheduling_event_spawn_exactly_one_child()
    {
        var world = await Seed("RACE", orderQty: 2, madeOnHand: 0);

        var outcomes = await Task.WhenAll(
            PickAllowingConflict(world.ParentId),
            PickAllowingConflict(world.ParentId));

        // At least one delivery got a handled business result. The other either did too, or lost a
        // concurrency conflict — never anything else.
        Assert.Contains(PickOutcome.InsufficientStock, outcomes.Where(o => o.HasValue).Select(o => o!.Value));

        var child = Assert.Single(await ChildrenOf(world.ParentId));
        Assert.Equal(2u, child.OrderItemQty);
        Assert.Single(PublishedRows<WorkOrderCreated>().Where(e => e.WorkOrderId == child.Id));

        // The redelivery the ladder would make. It must settle, not stall or duplicate.
        var redelivery = await Pick(world.ParentId);

        Assert.Equal(PickOutcome.InsufficientStock, redelivery.Outcome);
        Assert.Single(await ChildrenOf(world.ParentId));
        Assert.Equal(WorkOrderStatus.OnHold, await Status(world.ParentId));
    }

    /// <summary>
    /// A redelivery arriving <em>after</em> the first one committed takes the cheap path, and still
    /// spawns nothing. The order is already held, which is also handled rather than an error.
    /// </summary>
    [Fact]
    public async Task A_redelivered_scheduling_event_spawns_no_second_child()
    {
        var world = await Seed("REDELIVER", orderQty: 2, madeOnHand: 0);

        Assert.Equal(PickOutcome.InsufficientStock, (await Pick(world.ParentId)).Outcome);
        Assert.Equal(PickOutcome.InsufficientStock, (await Pick(world.ParentId)).Outcome);

        Assert.Single(await ChildrenOf(world.ParentId));
    }

    /// <summary>
    /// Putaway is idempotent on the child's own terminal transition — no new table. The credit and
    /// the transition commit together, so a second delivery finds a Completed order and the shelf
    /// does not rise twice.
    /// </summary>
    [Fact]
    public async Task A_redelivered_inspection_pass_stocks_the_shelf_only_once()
    {
        var world = await Seed("PUTAWAY", orderQty: 1, madeOnHand: 0);
        await Pick(world.ParentId);

        var child = Assert.Single(await ChildrenOf(world.ParentId));
        await Pick(child.Id);
        await Produce(child.Id, 1);
        await Inspect(child.Id, 1);

        Assert.Equal(PutawayOutcome.StockedAway, (await PutAway(child.Id)).Outcome);
        Assert.Equal(1u, await OnHand(world.MadeComponent));

        Assert.Equal(PutawayOutcome.AlreadyStocked, (await PutAway(child.Id)).Outcome);
        Assert.Equal(1u, await OnHand(world.MadeComponent));
    }

    // -------------------------------------------------------------------- the depth cap

    /// <summary>
    /// <strong>The runaway guard, against real data.</strong> A chain of made components deeper than
    /// the cap does not generate work forever: the order at the limit is routed to Fault with a
    /// reason a person can read, rather than held for a part nothing is scheduled to build.
    /// <para>
    /// The catalog here is deliberately absurd — a ladder of assemblies each made by the next — which
    /// is exactly the shape 13.2's explosion refuses on the read side and this refuses on the write
    /// side. Both share one constant, so they cannot disagree.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_chain_of_sub_assemblies_stops_at_the_depth_limit_and_faults()
    {
        // One level deeper than the cap allows, so the last spawn must be refused.
        var ladder = await SeedLadder("DEEP", WorkOrder.MaxSubAssemblyDepth + 1);

        var order = ladder.RootWorkOrderId;

        // Walk the chain: each order is short of the component the next one makes, so each spawns
        // one child, until the cap bites.
        for (var depth = 0; depth < WorkOrder.MaxSubAssemblyDepth; depth++)
        {
            Assert.Equal(PickOutcome.InsufficientStock, (await Pick(order)).Outcome);
            Assert.Equal(WorkOrderStatus.OnHold, await Status(order));

            var child = Assert.Single(await ChildrenOf(order));
            Assert.Equal(depth + 1, child.TreeDepth);
            order = child.Id;
        }

        // The order at the limit is short too — but nothing may be spawned beneath it.
        var refused = await Pick(order);

        Assert.Equal(PickOutcome.InsufficientStock, refused.Outcome);
        Assert.Contains("level limit", refused.Summary);
        Assert.Equal(WorkOrderStatus.Fault, await Status(order));
        Assert.Empty(await ChildrenOf(order));
    }

    // -------------------------------------------------------------------- the world sweep

    /// <summary>
    /// <strong>The sharpest edge in the story.</strong> A parent held for hours waiting on a child is
    /// exactly the shape 10.4's sweep retires — and retiring it would leave a child building parts
    /// for nobody. The sweep must leave the whole tree alone until every order in it is finished,
    /// and then take it in one go.
    /// </summary>
    [Fact]
    public async Task The_world_sweep_never_retires_an_order_with_a_live_child()
    {
        var world = await Seed("SWEEP", orderQty: 1, madeOnHand: 0);
        await Pick(world.ParentId);

        var child = Assert.Single(await ChildrenOf(world.ParentId));

        // Age everything past any plausible cutoff. The parent is OnHold and the child is Scheduled,
        // so on 10.4's rules alone the parent qualifies for retirement and the child does not.
        await AgeAllOrders(TimeSpan.FromDays(30));

        await Sweep();

        // Counted by presence, not by the sweep's own total: this fixture's database is shared
        // across the class, so other tests' finished orders are legitimately swept alongside.
        Assert.Equal(2, await OrderCount(world.ParentId, child.Id));

        // Finish the child. It is now terminal, but its *parent* is still OnHold — and this is the
        // case a naive "skip parents with children" rule would get wrong in the other direction, by
        // never retiring the tree at all.
        await Pick(child.Id);
        await Produce(child.Id, 1);
        await Inspect(child.Id, 1);
        await PutAway(child.Id);
        await AgeAllOrders(TimeSpan.FromDays(30));

        await Sweep();

        // Both, in one DELETE — which is the half a NO ACTION foreign key would have refused if the
        // predicate had retired the parent without the child.
        Assert.Equal(0, await OrderCount(world.ParentId, child.Id));
    }

    // ------------------------------------------------------------------ the parent's gate

    /// <summary>
    /// The acceptance criterion, against the database rather than an in-memory aggregate: a parent
    /// loaded from Postgres carries its children, so the completion guard can actually see them.
    /// A guard that silently passes because nobody loaded the data is not a guard.
    /// </summary>
    [Fact]
    public async Task A_parent_loaded_from_the_database_refuses_to_complete_while_a_child_runs()
    {
        var world = await Seed("GATE", orderQty: 1, madeOnHand: 0);
        await Pick(world.ParentId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();

        var parent = await repository.GetWithHistory(world.ParentId);

        Assert.NotNull(parent);
        Assert.Equal(1, parent!.LiveChildCount);

        // Force it to Inspection the way a superuser would, so the guard is the only thing standing
        // between it and Delivery.
        parent.SetStatus(WorkOrderStatus.Inspection, "x-test");

        var blocked = parent.AdvanceToNextStep("x-test");
        Assert.False(blocked.Success);
        Assert.Equal(TransitionErrorCode.ChildrenOutstanding, blocked.Code);
    }

    // ---------------------------------------------------------------------- read models

    /// <summary>
    /// Parent and child are visible to each other on the API, both directions. A spawned child
    /// nobody can see is a feature nobody can demo — and the board specifically needs its own flag,
    /// because a child inherits its parent's origin and the existing filter cannot tell them apart.
    /// </summary>
    [Fact]
    public async Task The_read_models_show_the_relationship_from_both_ends()
    {
        var world = await Seed("VISIBLE", orderQty: 2, madeOnHand: 0);
        await Pick(world.ParentId);

        var child = Assert.Single(await ChildrenOf(world.ParentId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();

        var parentDto = new Application.Data.WorkOrderDto((await repository.Get(world.ParentId))!);
        Assert.Null(parentDto.ParentWorkOrderId);
        Assert.Equal(1, parentDto.LiveChildCount);
        var listed = Assert.Single(parentDto.Children);
        Assert.Equal(child.Id, listed.Id);
        Assert.Equal(world.MadeComponent, listed.ForComponentId);
        Assert.True(listed.IsLive);

        var childDto = new Application.Data.WorkOrderDto((await repository.Get(child.Id))!);
        Assert.Equal(world.ParentId, childDto.ParentWorkOrderId);
        Assert.Equal(world.MadeComponent, childDto.ForComponentId);
        Assert.Empty(childDto.Children);

        var board = await repository.List([], [], 100);
        Assert.True(board.Single(row => row.Id == child.Id).IsSubAssembly);
        Assert.False(board.Single(row => row.Id == world.ParentId).IsSubAssembly);
    }

    // ----------------------------------------------------------------------- the drivers

    private async Task<PickResult> Pick(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MaterialPickingService>().PickMaterials(workOrderId);
    }

    /// <summary>
    /// A pick that tolerates losing a write race, returning null instead. Only the concurrency
    /// tests use it: everywhere else a throw out of picking is a real failure, and the difference
    /// between the two is exactly what makes this helper worth naming rather than inlining.
    /// </summary>
    private async Task<PickOutcome?> PickAllowingConflict(Guid workOrderId)
    {
        try
        {
            return (await Pick(workOrderId)).Outcome;
        }
        catch (DbUpdateException)
        {
            // The unique index or the xmin token refused this delivery. 8.2 classifies both as
            // transient and the ladder replays the message.
            return null;
        }
    }

    private async Task<PickResult> Pick(Guid workOrderId, int attempt, uint demandQty)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MaterialPickingService>()
            .PickMaterials(workOrderId, attempt, demandQty);
    }

    private async Task<ProductionResult> Produce(Guid workOrderId, int attempt)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ProductionService>().Produce(workOrderId, attempt);
    }

    private async Task<InspectionResult> Inspect(Guid workOrderId, int attempt)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InspectionService>().InspectAttempt(workOrderId, attempt);
    }

    /// <summary>The branch <c>InspectionPassedHandler</c> takes before it reaches for a carrier.</summary>
    private async Task<PutawayResult> PutAway(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StockPutawayService>()
            .TryPutAway(workOrderId, []);
    }

    private async Task<ParentReleaseResult> ReleaseParent(Guid completedWorkOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubAssemblyService>()
            .ReleaseParentOfCompletedChild(completedWorkOrderId);
    }

    private async Task<WorldSweepCounts> Sweep()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWorldRepository>()
            .Sweep(DateTime.UtcNow.AddHours(-1));
    }

    // ------------------------------------------------------------------------- the reads

    private async Task<List<WorkOrder>> ChildrenOf(Guid parentId)
    {
        await using var context = _fixture.NewContext();
        return await context.WorkOrders
            .AsNoTracking()
            .Include(order => order.OrderedItem)
            .Where(order => order.ParentWorkOrderId == parentId)
            .OrderBy(order => order.CreatedUtc)
            .ToListAsync();
    }

    private async Task<WorkOrderStatus> Status(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return (await context.WorkOrders.AsNoTracking().SingleAsync(order => order.Id == workOrderId)).CurrentStatus;
    }

    private async Task<uint> OnHand(string componentId)
    {
        await using var context = _fixture.NewContext();
        return (await context.Components.AsNoTracking().SingleAsync(c => c.ComponentId == componentId)).OnHand;
    }

    private async Task<List<Domain.Models.Shipping.Shipment>> Shipments(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.Shipments.AsNoTracking()
            .Where(shipment => shipment.WorkOrderId == workOrderId).ToListAsync();
    }

    private async Task<int> OrderCount(params Guid[] ids)
    {
        await using var context = _fixture.NewContext();
        return await context.WorkOrders.CountAsync(order => ids.Contains(order.Id));
    }

    /// <summary>
    /// Backdates every order so the sweep's cutoff bites. Written as raw SQL because
    /// <c>UpdatedUtc</c> is domain-owned — there is deliberately no setter, and inventing one for a
    /// test would put a lie in the aggregate.
    /// </summary>
    private async Task AgeAllOrders(TimeSpan by)
    {
        await using var context = _fixture.NewContext();
        await context.Database.ExecuteSqlAsync(
            $"""UPDATE work_orders SET "UpdatedUtc" = "UpdatedUtc" - {by}""");
    }

    private IReadOnlyList<T> PublishedFor<T>(Guid workOrderId) where T : IntegrationEvent
        => PublishedRows<T>().Where(@event => WorkOrderIdOf(@event) == workOrderId).ToList();

    private IReadOnlyList<T> PublishedRows<T>() where T : IntegrationEvent => _fixture.Published.OfType<T>();

    private static Guid WorkOrderIdOf(IntegrationEvent @event) => @event switch
    {
        WorkOrderCreated e => e.WorkOrderId,
        WorkOrderScheduled e => e.WorkOrderId,
        MaterialsReserved e => e.WorkOrderId,
        ProductionCompleted e => e.WorkOrderId,
        InspectionPassed e => e.WorkOrderId,
        WorkOrderCompleted e => e.WorkOrderId,
        WorkOrderFaulted e => e.WorkOrderId,
        _ => Guid.Empty
    };

    // -------------------------------------------------------------------------- the seed

    /// <summary>
    /// One saleable product built from a bought part and a <em>made</em> one, plus the sub-assembly
    /// product that makes it, and a scheduled order. Every test gets its own tag so the shelves
    /// never interfere.
    /// </summary>
    private async Task<World> Seed(string tag, uint orderQty, uint madeOnHand)
    {
        await using var context = _fixture.NewContext();

        var boughtSeed = 500u;
        var bought = new Component($"CMP-{tag}-PANEL", "Brass Panel", boughtSeed);
        var subPart = new Component($"CMP-{tag}-RELAY", "Relay Bank", 500);

        var maker = new Product($"SUBASM-{tag}", $"{tag} Control Stack Assembly");
        maker.AddBomLine(subPart, qtyPerUnit: 1);

        // The link that makes it a multi-level BOM (13.2): one nullable column.
        var made = new Component($"CMP-{tag}-STACK", "Control Stack", madeOnHand, maker.ItemId);

        var parentProduct = new Product($"PRD-{tag}", $"{tag} Automaton");
        parentProduct.AddBomLine(bought, qtyPerUnit: 1);
        parentProduct.AddBomLine(made, qtyPerUnit: 1);

        var order = new WorkOrder("seed", parentProduct, orderQty);
        order.AdvanceToNextStep("seed");    // Intake → Scheduled

        context.Products.AddRange(parentProduct, maker);
        context.Components.AddRange(bought, subPart, made);
        context.WorkOrders.Add(order);
        await context.SaveChangesAsync();

        return new World(order.Id, made.ComponentId, bought.ComponentId, maker.ItemId, boughtSeed);
    }

    /// <summary>
    /// A deliberately absurd catalog: <paramref name="levels"/> assemblies, each built from the
    /// component the next one makes, and every shelf empty. Exactly the shape the depth cap exists
    /// for — one that would otherwise schedule work forever.
    /// </summary>
    private async Task<Ladder> SeedLadder(string tag, int levels)
    {
        await using var context = _fixture.NewContext();

        var products = new List<Product>();
        var components = new List<Component>();

        // Built bottom-up, because a component names the product that makes it.
        for (var level = levels; level >= 1; level--)
        {
            var maker = new Product($"SUBASM-{tag}-{level}", $"{tag} Assembly L{level}");
            products.Add(maker);

            var made = new Component($"CMP-{tag}-{level}", $"{tag} Part L{level}", onHand: 0, maker.ItemId);
            components.Add(made);
        }

        // Each assembly consumes the part one level down; the deepest consumes nothing but a bought
        // part, so the walk is stopped by the cap rather than by running out of catalog.
        var floor = new Component($"CMP-{tag}-FLOOR", "Bar Stock", onHand: 0);
        components.Add(floor);

        for (var level = 1; level <= levels; level++)
        {
            var maker = products.Single(product => product.ItemId == $"SUBASM-{tag}-{level}");
            var below = level < levels
                ? components.Single(component => component.ComponentId == $"CMP-{tag}-{level + 1}")
                : floor;
            maker.AddBomLine(below, qtyPerUnit: 1);
        }

        var root = new Product($"PRD-{tag}", $"{tag} Automaton");
        root.AddBomLine(components.Single(component => component.ComponentId == $"CMP-{tag}-1"), qtyPerUnit: 1);
        products.Add(root);

        var order = new WorkOrder("seed", root, 1);
        order.AdvanceToNextStep("seed");

        context.Products.AddRange(products);
        context.Components.AddRange(components);
        context.WorkOrders.Add(order);
        await context.SaveChangesAsync();

        return new Ladder(order.Id);
    }

    private sealed record World(
        Guid ParentId,
        string MadeComponent,
        string BoughtComponent,
        string MakerProductId,
        uint BoughtSeed);

    private sealed record Ladder(Guid RootWorkOrderId);
}
