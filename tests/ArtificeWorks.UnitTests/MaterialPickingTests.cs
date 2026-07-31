using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.Logging.Abstractions;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// 13.1's arithmetic, without a database: <em>how much</em> a pick asks for, and where that number
/// comes from. The concurrency guarantees around it are database properties and stay in the
/// integration suite; this is the rule that decides whether a rebuild bankrupts the shelf or
/// under-buys, and it is worth pinning where it is cheap to run.
/// </summary>
public class MaterialPickingTests
{
    private const string Author = "x-unit";

    /// <summary>
    /// The story's central claim. Attempt 1 buys parts for everything ordered; a rebuild buys parts
    /// for the shortfall the rework event named — not the order quantity (which would over-draw) and
    /// not a re-read of the order's current outstanding count (which a redelivery could compute
    /// differently, leaving the unique index guarding two different requests).
    /// </summary>
    [Theory]
    [InlineData(1, null, 15u)]  // attempt 1: 5 ordered × 3 per unit
    [InlineData(2, 2u, 6u)]     // rebuild:   2 outstanding × 3 per unit
    [InlineData(3, 1u, 3u)]
    public async Task A_pick_demands_the_quantity_it_was_asked_for_not_the_orders(
        int attempt, uint? demandQty, uint expectedDraw)
    {
        var (service, reservations, published) = ServiceFor(orderQty: 5, qtyPerUnit: 3);

        var result = await service.PickMaterials(reservations.WorkOrderId, attempt, demandQty);

        Assert.Equal(PickOutcome.Picked, result.Outcome);
        Assert.Equal(expectedDraw, reservations.Committed.Single().Demand.Single().Quantity);
        Assert.Equal(attempt, reservations.Committed.Single().AttemptNumber);

        // And the announcement carries both, because production reads the attempt off it.
        var reserved = Assert.Single(published.OfType<MaterialsReserved>());
        Assert.Equal(attempt, reserved.AttemptNumber);
        Assert.Equal(demandQty ?? 5u, reserved.Quantity);
    }

    /// <summary>
    /// The one-argument overload is the initial pick, and it must stay that: a caller who does not
    /// name an attempt is a caller who has a freshly scheduled order.
    /// </summary>
    [Fact]
    public async Task The_default_entry_point_is_attempt_one_for_the_whole_order()
    {
        var (service, reservations, _) = ServiceFor(orderQty: 4, qtyPerUnit: 1);

        Assert.Equal(PickOutcome.Picked, (await service.PickMaterials(reservations.WorkOrderId)).Outcome);

        var committed = reservations.Committed.Single();
        Assert.Equal(MaterialPickingService.InitialAttempt, committed.AttemptNumber);
        Assert.Equal(4u, committed.Demand.Single().Quantity);
    }

    /// <summary>
    /// Inspection never publishes a rework event with nothing outstanding — it goes to rework
    /// precisely because units are short — but a demand of zero must be a handled no-op rather than
    /// an exception, because nacking would put a message that can never succeed onto the ladder.
    /// </summary>
    [Fact]
    public async Task A_pick_for_no_units_reserves_nothing_and_does_not_throw()
    {
        var (service, reservations, published) = ServiceFor(orderQty: 2, qtyPerUnit: 1);

        var result = await service.PickMaterials(reservations.WorkOrderId, attemptNumber: 2, demandQty: 0);

        Assert.Equal(PickOutcome.NothingToPick, result.Outcome);
        Assert.Empty(reservations.Committed);
        Assert.Empty(published.OfType<MaterialsReserved>());
    }

    /// <summary>The pre-check asks about this attempt, not about the order — otherwise every rebuild
    /// would be mistaken for a redelivery of the first pick and silently skipped.</summary>
    [Fact]
    public async Task An_earlier_attempts_reservation_does_not_make_a_rebuild_look_like_a_duplicate()
    {
        var (service, reservations, _) = ServiceFor(orderQty: 3, qtyPerUnit: 1);

        Assert.Equal(PickOutcome.Picked, (await service.PickMaterials(reservations.WorkOrderId)).Outcome);

        // Same order, next attempt: a fresh pick, not a duplicate.
        var rebuild = await service.PickMaterials(reservations.WorkOrderId, attemptNumber: 2, demandQty: 1);
        Assert.Equal(PickOutcome.Picked, rebuild.Outcome);

        // Same order, same attempt: a duplicate, and nothing is drawn for it.
        var redelivery = await service.PickMaterials(reservations.WorkOrderId, attemptNumber: 2, demandQty: 1);
        Assert.Equal(PickOutcome.AlreadyPicked, redelivery.Outcome);

        Assert.Equal([1, 2], reservations.Committed.Select(c => c.AttemptNumber));
    }

    // -------------------------------------------------------------------------- helpers

    private static (MaterialPickingService Service, FakeReservationRepository Reservations, RecordingPublisher Published)
        ServiceFor(uint orderQty, uint qtyPerUnit)
    {
        var product = TestData.DefaultProduct();
        product.AddBomLine(new Component("CMP-CHASSIS", "Chassis", onHand: 1_000), qtyPerUnit);

        var workOrder = new WorkOrder(Author, product, orderQty);
        workOrder.AdvanceToNextStep(Author); // Intake -> Scheduled

        var reservations = new FakeReservationRepository(workOrder.Id);
        var published = new RecordingPublisher();

        var service = new MaterialPickingService(
            new SingleWorkOrderRepository(workOrder),
            new SingleProductRepository(product),
            reservations,
            published,
            TestData.Metrics(),
            NullLogger<MaterialPickingService>.Instance);

        return (service, reservations, published);
    }

    /// <summary>
    /// Records what each pick asked for, and enforces the one rule that matters here in memory: one
    /// reservation per (order, attempt). The real guarantee is a unique index and is proved against
    /// Postgres; this fake only has to be faithful enough that the duplicate path is reachable.
    /// </summary>
    private sealed class FakeReservationRepository(Guid workOrderId) : IMaterialReservationRepository
    {
        public Guid WorkOrderId { get; } = workOrderId;

        public List<(int AttemptNumber, IReadOnlyList<ComponentDemand> Demand)> Committed { get; } = [];

        private readonly Dictionary<(Guid, int), MaterialReservation> _rows = [];

        public Task<MaterialReservation?> GetForAttempt(
            Guid orderId, int attemptNumber, CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.GetValueOrDefault((orderId, attemptNumber)));

        public async Task<ReservationCommitResult> TryReserve(
            Guid orderId,
            int attemptNumber,
            IReadOnlyList<ComponentDemand> demand,
            Func<MaterialReservation, Task>? stageWithReservation = null,
            CancellationToken cancellationToken = default)
        {
            if (_rows.ContainsKey((orderId, attemptNumber)))
            {
                return ReservationCommitResult.AlreadyReserved();
            }

            var reservation = new MaterialReservation(orderId, attemptNumber, demand);
            _rows[(orderId, attemptNumber)] = reservation;
            Committed.Add((attemptNumber, demand));

            if (stageWithReservation is not null)
            {
                await stageWithReservation(reservation);
            }

            return ReservationCommitResult.Reserved(reservation);
        }
    }

    private sealed class SingleProductRepository(Product product) : IProductRepository
    {
        public Task<Product?> Get(string id) => Task.FromResult<Product?>(product.ItemId == id ? product : null);
        public Task<Product?> GetWithBom(string id) => Get(id);
        public Task<IReadOnlyList<Product>> List() => Task.FromResult<IReadOnlyList<Product>>([product]);
        public Task<Product> Add(Product added) => Task.FromResult(added);
    }

    private sealed class SingleWorkOrderRepository(WorkOrder workOrder) : IWorkOrderRepository
    {
        public Task<WorkOrder?> Get(Guid id) => Task.FromResult<WorkOrder?>(workOrder.Id == id ? workOrder : null);
        public Task<WorkOrder?> GetWithHistory(Guid id) => Get(id);
        public Task<WorkOrder> Add(WorkOrder order) => Task.FromResult(order);
        public Task Update(WorkOrder order) => Task.CompletedTask;
        public Task<IReadOnlyList<Application.Data.WorkOrderListItemDto>> List(
            IReadOnlyCollection<WorkOrderStatus> statuses,
            IReadOnlyCollection<WorkOrderOrigin> origins,
            int limit)
            => Task.FromResult<IReadOnlyList<Application.Data.WorkOrderListItemDto>>([]);
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
