using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.SubAssemblies;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.Logging.Abstractions;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// 13.3 without a database: the rules that decide <em>whether</em> the factory schedules a
/// sub-assembly, <em>how many</em> it builds, and <em>when</em> it refuses to.
/// <para>
/// The loop itself — spawn, run, put away, resume — is a set of database properties (a filtered
/// unique index, a credit committed with a terminal transition, a delete that must not orphan a
/// child) and lives in the integration suite. What is here is the arithmetic and the guards, which
/// are cheap to run and are where getting it wrong is a runaway rather than a slow demo.
/// </para>
/// </summary>
public class SubAssemblyTests
{
    private const string Author = "x-unit";

    // ------------------------------------------------------------------ the aggregate

    /// <summary>
    /// A child cannot exist without all three parts of its link, and the domain — not a caller —
    /// decides that it inherits its parent's origin. A visitor who orders a Courier has caused every
    /// unit of work beneath it, so splitting a third value out of a two-valued metric dimension to
    /// say "the factory asked itself" would make every origin-split panel wrong in a new way.
    /// </summary>
    [Theory]
    [InlineData(WorkOrderOrigin.Visitor)]
    [InlineData(WorkOrderOrigin.Simulated)]
    public void A_child_carries_its_link_and_inherits_its_parents_origin(WorkOrderOrigin origin)
    {
        var parent = new WorkOrder(Author, TestData.DefaultProduct(), 5, origin: origin);

        var child = WorkOrder.ForSubAssembly(
            parent, TestData.SomeOtherProduct(), "CMP-MADE", qty: 3, parentAttemptNumber: 2, Author);

        Assert.Equal(parent.Id, child.ParentWorkOrderId);
        Assert.Equal("CMP-MADE", child.ForComponentId);
        Assert.Equal(2, child.ParentAttemptNumber);
        Assert.Equal(3u, child.OrderItemQty);
        Assert.Equal(origin, child.Origin);
        Assert.True(child.IsSubAssemblyOrder);

        // And the parent knows about it in the same unit of work, which is what makes the
        // completion guard below able to see it without a second query.
        Assert.Equal(child, Assert.Single(parent.Children));
        Assert.Equal(1, parent.LiveChildCount);
    }

    /// <summary>Depth is what a top-level order has none of, and each level adds exactly one.</summary>
    [Fact]
    public void Depth_counts_levels_below_the_order_a_customer_placed()
    {
        var parent = new WorkOrder(Author, TestData.DefaultProduct(), 1);
        Assert.Equal(0, parent.TreeDepth);

        var child = WorkOrder.ForSubAssembly(parent, TestData.SomeOtherProduct(), "CMP-A", 1, 1, Author);
        var grandchild = WorkOrder.ForSubAssembly(child, TestData.DefaultProduct(), "CMP-B", 1, 1, Author);

        Assert.Equal(1, child.TreeDepth);
        Assert.Equal(2, grandchild.TreeDepth);
    }

    /// <summary>
    /// The runaway guard, at the aggregate. A catalog that reaches itself is an infinite child-order
    /// generator pointed at a shared world with a rate-limited chaos endpoint and no auth, so the
    /// domain refuses rather than trusting every caller to check first.
    /// </summary>
    [Fact]
    public void A_chain_at_the_depth_limit_refuses_to_spawn_another()
    {
        var order = new WorkOrder(Author, TestData.DefaultProduct(), 1);

        // Walk down to the last legal level.
        for (var depth = 0; depth < WorkOrder.MaxSubAssemblyDepth; depth++)
        {
            Assert.True(order.CanSpawnSubAssembly);
            order = WorkOrder.ForSubAssembly(order, TestData.SomeOtherProduct(), $"CMP-{depth}", 1, 1, Author);
        }

        Assert.Equal(WorkOrder.MaxSubAssemblyDepth, order.TreeDepth);
        Assert.False(order.CanSpawnSubAssembly);
        Assert.Throws<InvalidOperationException>(
            () => WorkOrder.ForSubAssembly(order, TestData.SomeOtherProduct(), "CMP-TOO-DEEP", 1, 1, Author));
    }

    /// <summary>
    /// The acceptance criterion, proved in the domain rather than implied by the pipeline's shape.
    /// A parent waiting on a child is held at picking in practice, several stages before Delivery —
    /// which is exactly why this guard exists: it catches the bug the pipeline did not foresee.
    /// </summary>
    [Fact]
    public void A_parent_with_live_children_cannot_ship_or_complete()
    {
        var parent = new WorkOrder(Author, TestData.DefaultProduct(), 1);
        var child = WorkOrder.ForSubAssembly(parent, TestData.SomeOtherProduct(), "CMP-MADE", 1, 1, Author);

        // Intake → Scheduled → InProcess → Inspection are all still legal: the gate is on leaving
        // inspection, not on doing the work.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            Assert.True(parent.AdvanceToNextStep(Author).Success);
        }
        Assert.Equal(WorkOrderStatus.Inspection, parent.CurrentStatus);

        var blocked = parent.AdvanceToNextStep(Author);
        Assert.False(blocked.Success);
        Assert.Equal(TransitionErrorCode.ChildrenOutstanding, blocked.Code);
        Assert.Equal(WorkOrderStatus.Inspection, parent.CurrentStatus);

        // Finish the child, and the gate opens. Nothing else changed.
        Complete(child);
        Assert.Equal(0, parent.LiveChildCount);
        Assert.True(parent.AdvanceToNextStep(Author).Success);
        Assert.Equal(WorkOrderStatus.Delivery, parent.CurrentStatus);
    }

    /// <summary>
    /// A <em>faulted</em> child still counts as live. It is stuck, not finished, and a parent that
    /// sailed past one would ship without the part it was waiting for — the quiet failure this whole
    /// guard exists to make loud.
    /// </summary>
    [Fact]
    public void A_faulted_child_still_holds_the_gate_shut()
    {
        var parent = new WorkOrder(Author, TestData.DefaultProduct(), 1);
        var child = WorkOrder.ForSubAssembly(parent, TestData.SomeOtherProduct(), "CMP-MADE", 1, 1, Author);

        Assert.True(child.Fault(Author, "its own materials never arrived").Success);

        Assert.Equal(1, parent.LiveChildCount);
        Assert.False(parent.IsTerminal);

        // A cancelled child, by contrast, is finished: somebody decided it was not needed.
        Assert.True(child.Cancel(Author).Success);
        Assert.Equal(0, parent.LiveChildCount);
    }

    // ---------------------------------------------------------------- the shortfall

    /// <summary>
    /// The child builds the gap, not the whole demand: whatever is already on the shelf will be
    /// drawn by the re-pick, so building it again would be waste the factory pays for twice.
    /// </summary>
    [Theory]
    [InlineData(10u, 4u, 6u)]
    [InlineData(3u, 0u, 3u)]
    [InlineData(1u, 0u, 1u)]
    public void The_shortfall_is_demand_minus_on_hand(uint demanded, uint onHand, uint expected)
    {
        Assert.Equal(expected, new ShortComponent("CMP-MADE", demanded, onHand).Shortfall);
    }

    // ------------------------------------------------------------------- the plan

    /// <summary>
    /// The headline. A pick short of a component the factory <em>makes</em> schedules it: one child
    /// order, for the shortfall, on the maker product, inheriting the parent's origin — and it is
    /// announced with the two events an ordinary order is announced with, so the board, the feed and
    /// the pipeline need to know nothing about sub-assemblies.
    /// </summary>
    [Fact]
    public async Task A_short_made_component_is_scheduled_as_a_child_order()
    {
        var world = new World();
        var parent = world.Order(qty: 4);

        var plan = await world.Service().PlanForShortages(
            parent,
            attemptNumber: 1,
            [new ShortComponent(World.MadeComponent, Demanded: 4, OnHand: 1)],
            world.Bom);

        Assert.Equal(SubAssemblyRequestOutcome.Requested, plan.Outcome);

        var child = Assert.Single(plan.Children);
        Assert.Equal(parent.Id, child.ParentWorkOrderId);
        Assert.Equal(World.MadeComponent, child.ForComponentId);
        Assert.Equal(World.MakerProduct, child.OrderedItem.ItemId);
        Assert.Equal(3u, child.OrderItemQty);            // 4 demanded − 1 on the shelf
        Assert.Equal(1, child.ParentAttemptNumber);
        Assert.Equal(1, child.TreeDepth);

        // Straight past Intake: nothing waits on a human to approve a part the factory has already
        // decided it needs.
        Assert.Equal(WorkOrderStatus.Scheduled, child.CurrentStatus);

        var created = Assert.Single(world.Published.OfType<WorkOrderCreated>());
        var scheduled = Assert.Single(world.Published.OfType<WorkOrderScheduled>());
        Assert.Equal(child.Id, created.WorkOrderId);
        Assert.Equal(child.Id, scheduled.WorkOrderId);
        Assert.Equal(3u, scheduled.Quantity);
        Assert.Equal(MaterialPickingService.InitialAttempt, scheduled.AttemptNumber);
    }

    /// <summary>
    /// The unchanged path, and still the common one: ~85% of BOM lines are bought parts, and a
    /// factory short of brass panels has nobody to ask. The order holds exactly as it has since 5.3.
    /// </summary>
    [Fact]
    public async Task A_short_bought_component_schedules_nothing()
    {
        var world = new World();

        var plan = await world.Service().PlanForShortages(
            world.Order(),
            attemptNumber: 1,
            [new ShortComponent(World.BoughtComponent, Demanded: 9, OnHand: 2)],
            world.Bom);

        Assert.Equal(SubAssemblyRequestOutcome.NothingToMake, plan.Outcome);
        Assert.Empty(plan.Children);
        Assert.Empty(world.Published.OfType<WorkOrderCreated>());
    }

    /// <summary>One child per short made component, and the bought ones alongside them are ignored.</summary>
    [Fact]
    public async Task Several_short_made_components_each_get_their_own_child()
    {
        var world = new World();

        var plan = await world.Service().PlanForShortages(
            world.Order(),
            attemptNumber: 1,
            [
                new ShortComponent(World.BoughtComponent, 5, 0),
                new ShortComponent(World.MadeComponent, 5, 0),
                new ShortComponent(World.OtherMadeComponent, 2, 0),
            ],
            world.Bom);

        Assert.Equal(SubAssemblyRequestOutcome.Requested, plan.Outcome);
        Assert.Equal(
            [World.MadeComponent, World.OtherMadeComponent],
            plan.Requested.Select(request => request.ComponentId).Order());
    }

    /// <summary>
    /// The pre-check in front of the filtered unique index. A redelivered scheduling event — and a
    /// parent re-picking while a sibling is still building — must not raise a second order for work
    /// already under way; it simply holds again and waits.
    /// </summary>
    [Fact]
    public async Task A_component_already_being_made_is_not_requested_twice()
    {
        var world = new World();
        world.OpenRequests.Add(World.MadeComponent);

        var plan = await world.Service().PlanForShortages(
            world.Order(),
            attemptNumber: 1,
            [new ShortComponent(World.MadeComponent, 5, 0)],
            world.Bom);

        Assert.Equal(SubAssemblyRequestOutcome.AlreadyRequested, plan.Outcome);
        Assert.Empty(plan.Children);
        Assert.Empty(world.Published.OfType<WorkOrderScheduled>());
    }

    /// <summary>
    /// The depth cap, at the workflow. The caller faults the order on this outcome rather than
    /// holding it: the catalog is wrong, no amount of waiting fixes it, and an order that waits
    /// forever is a stall that looks like a bug.
    /// </summary>
    [Fact]
    public async Task A_chain_at_the_limit_plans_nothing_and_says_so()
    {
        var world = new World();

        var order = world.Order();
        for (var depth = 0; depth < WorkOrder.MaxSubAssemblyDepth; depth++)
        {
            order = WorkOrder.ForSubAssembly(order, world.Maker, $"CMP-{depth}", 1, 1, Author);
        }

        var plan = await world.Service().PlanForShortages(
            order, attemptNumber: 1, [new ShortComponent(World.MadeComponent, 5, 0)], world.Bom);

        Assert.Equal(SubAssemblyRequestOutcome.TooDeep, plan.Outcome);
        Assert.Empty(plan.Children);
        Assert.Empty(world.Published.OfType<WorkOrderScheduled>());
    }

    // ----------------------------------------------------------------- the release

    /// <summary>
    /// The loop's far half: a finished child releases the parent and re-schedules it at the attempt
    /// its short pick was buying for, for what it still owes. Both numbers travel on the event so a
    /// redelivery computes an identical request rather than re-reading state.
    /// </summary>
    [Fact]
    public async Task A_completed_child_releases_its_parent_and_re_picks_the_same_attempt()
    {
        var world = new World();
        var parent = world.Order(qty: 4);
        var child = WorkOrder.ForSubAssembly(parent, world.Maker, World.MadeComponent, 4, 1, Author);
        Complete(child);

        Assert.True(parent.AdvanceToNextStep(Author).Success);     // → Scheduled, as a real one is
        Assert.True(parent.SetHold(Author, "waiting on sub-assembly").Success);

        world.Orders[parent.Id] = parent;
        world.Orders[child.Id] = child;

        var result = await world.Service().ReleaseParentOfCompletedChild(child.Id);

        Assert.Equal(ParentReleaseOutcome.Released, result.Outcome);
        Assert.Equal(WorkOrderStatus.Scheduled, parent.CurrentStatus);

        var scheduled = Assert.Single(world.Published.OfType<WorkOrderScheduled>());
        Assert.Equal(parent.Id, scheduled.WorkOrderId);
        Assert.Equal(1, scheduled.AttemptNumber);   // nothing built yet, so still attempt 1
        Assert.Equal(4u, scheduled.Quantity);
    }

    /// <summary>
    /// With several children in flight, only the last one to finish resumes the parent — and the
    /// others take this branch rather than erroring. That is the whole of the "wait for all of them"
    /// logic: no query counting live children, no new state, just a released parent that re-picks and
    /// holds again if it is still short.
    /// </summary>
    [Fact]
    public async Task A_child_completing_against_an_unheld_parent_does_nothing()
    {
        var world = new World();
        var parent = world.Order();
        var child = WorkOrder.ForSubAssembly(parent, world.Maker, World.MadeComponent, 1, 1, Author);
        Complete(child);

        world.Orders[parent.Id] = parent;
        world.Orders[child.Id] = child;

        var result = await world.Service().ReleaseParentOfCompletedChild(child.Id);

        Assert.Equal(ParentReleaseOutcome.NotHeld, result.Outcome);
        Assert.Empty(world.Published.OfType<WorkOrderScheduled>());
    }

    /// <summary>
    /// Nearly every delivery of <c>work-order.completed</c> is a customer's order finishing. That
    /// must be one cheap read and nothing else — this key binds to the pipeline now, so a handler
    /// that did real work per completion would tax every order in the factory.
    /// </summary>
    [Fact]
    public async Task An_ordinary_order_completing_releases_nothing()
    {
        var world = new World();
        var order = world.Order();
        Complete(order);
        world.Orders[order.Id] = order;

        var result = await world.Service().ReleaseParentOfCompletedChild(order.Id);

        Assert.Equal(ParentReleaseOutcome.NotASubAssembly, result.Outcome);
        Assert.Empty(world.Published.OfType<WorkOrderScheduled>());
    }

    // -------------------------------------------------------------------- helpers

    /// <summary>Walks an order to Completed the long way, so its status is reached, not assigned.</summary>
    private static void Complete(WorkOrder order)
    {
        while (order.CurrentStatus != WorkOrderStatus.Completed)
        {
            Assert.True(order.AdvanceToNextStep(Author).Success);
        }
    }

    /// <summary>
    /// A tiny catalog with one bought component and two made ones, plus in-memory repositories. Just
    /// enough for the decisions under test; every guarantee that needs a database is asserted in the
    /// integration suite instead.
    /// </summary>
    private sealed class World
    {
        public const string BoughtComponent = "CMP-BOUGHT";
        public const string MadeComponent = "CMP-MADE";
        public const string OtherMadeComponent = "CMP-MADE-TWO";
        public const string MakerProduct = "SUBASM-MAKER";
        public const string OtherMakerProduct = "SUBASM-MAKER-TWO";

        public Product Parent { get; } = new("PRD-PARENT", "Parent Automaton");
        public Product Maker { get; } = new(MakerProduct, "Maker Assembly");
        public Product OtherMaker { get; } = new(OtherMakerProduct, "Other Maker Assembly");

        public RecordingPublisher Published { get; } = new();
        public Dictionary<Guid, WorkOrder> Orders { get; } = [];
        public List<string> OpenRequests { get; } = [];

        public IReadOnlyList<BomLine> Bom => Parent.BillOfMaterials;

        public World()
        {
            Parent.AddBomLine(new Component(BoughtComponent, "A Bought Part", 100), 1);
            Parent.AddBomLine(new Component(MadeComponent, "A Made Part", 0, MakerProduct), 1);
            Parent.AddBomLine(new Component(OtherMadeComponent, "Another Made Part", 0, OtherMakerProduct), 1);
        }

        public WorkOrder Order(uint qty = 1)
        {
            var order = new WorkOrder(Author, Parent, qty);
            Orders[order.Id] = order;
            return order;
        }

        public SubAssemblyService Service() => new(
            new DictionaryWorkOrderRepository(Orders, OpenRequests),
            new StaticProductRepository(Parent, Maker, OtherMaker),
            Published,
            TestData.Metrics(),
            NullLogger<SubAssemblyService>.Instance);
    }

    private sealed class DictionaryWorkOrderRepository(
        Dictionary<Guid, WorkOrder> orders, List<string> openRequests) : IWorkOrderRepository
    {
        public Task<WorkOrder?> Get(Guid id) =>
            Task.FromResult(orders.TryGetValue(id, out var order) ? order : null);

        public Task<WorkOrder?> GetWithHistory(Guid id) => Get(id);
        public Task<WorkOrder> Add(WorkOrder workOrder) => Task.FromResult(workOrder);
        public Task Update(WorkOrder workOrder) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkOrderListItemDto>> List(
            IReadOnlyCollection<WorkOrderStatus> statuses,
            IReadOnlyCollection<WorkOrderOrigin> origins,
            int limit)
            => Task.FromResult<IReadOnlyList<WorkOrderListItemDto>>([]);

        public Task<IReadOnlyList<string>> ListOpenSubAssemblyRequests(
            Guid parentWorkOrderId, int parentAttemptNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(openRequests);
    }

    private sealed class StaticProductRepository(params Product[] products) : IProductRepository
    {
        public Task<Product?> Get(string id) =>
            Task.FromResult(products.FirstOrDefault(product => product.ItemId == id));

        public Task<Product?> GetWithBom(string id) => Get(id);
        public Task<IReadOnlyList<Product>> List() => Task.FromResult<IReadOnlyList<Product>>(products);
        public Task<IReadOnlyList<Product>> ListWithBoms() => Task.FromResult<IReadOnlyList<Product>>(products);

        public Task<IReadOnlyList<string>> ListSubAssemblyProductIds() =>
            Task.FromResult<IReadOnlyList<string>>([World.MakerProduct, World.OtherMakerProduct]);

        public Task<Product> Add(Product product) => Task.FromResult(product);
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly List<IntegrationEvent> _events = [];

        public IReadOnlyList<T> OfType<T>() where T : IntegrationEvent => _events.OfType<T>().ToList();

        public Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default)
            where T : IntegrationEvent
        {
            _events.Add(@event);
            return Task.CompletedTask;
        }
    }
}
