using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// The chaos service's door check (12.1): the bounded blast radius is enforced when a fault is
/// armed, not merely by convention. Which orders can be broken, and which are refused, is pure
/// state reasoning, so it is proved here without a database.
/// </summary>
public class ChaosTests
{
    [Fact]
    public async Task Arming_a_live_in_flight_order_succeeds_and_records_the_fault()
    {
        var order = InFlightOrder(WorkOrderStatus.InProcess);
        var faults = new FakeInjectedFaultRepository();
        var service = ServiceFor(order, faults);

        var result = await service.Arm(order.Id, InjectedFaultKind.FailInspection, "visitor");

        Assert.Equal(ChaosArmOutcome.Armed, result.Outcome);
        Assert.NotNull(result.Fault);
        Assert.Equal(order.Id, result.Fault!.WorkOrderId);
        Assert.Equal(InjectedFaultKind.FailInspection, result.Fault.Kind);
        Assert.True(await faults.IsArmed(order.Id, InjectedFaultKind.FailInspection));
    }

    [Fact]
    public async Task Arming_an_order_that_does_not_exist_is_a_not_found()
    {
        var faults = new FakeInjectedFaultRepository();
        var service = new ChaosService(faults, new EmptyWorkOrderRepository(), NullLogger<ChaosService>.Instance);

        var result = await service.Arm(Guid.NewGuid(), InjectedFaultKind.FailInspection, "visitor");

        Assert.Equal(ChaosArmOutcome.TargetNotFound, result.Outcome);
        Assert.Null(result.Fault);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    [InlineData(WorkOrderStatus.Fault)]
    public async Task Arming_an_order_that_cannot_be_broken_is_refused(WorkOrderStatus status)
    {
        var order = InFlightOrder(status);
        var faults = new FakeInjectedFaultRepository();
        var service = ServiceFor(order, faults);

        var result = await service.Arm(order.Id, InjectedFaultKind.FailInspection, "visitor");

        Assert.Equal(ChaosArmOutcome.TargetNotInjectable, result.Outcome);
        // Nothing is armed against an order that cannot be broken.
        Assert.False(await faults.IsArmed(order.Id, InjectedFaultKind.FailInspection));
    }

    [Fact]
    public async Task A_fail_inspection_fault_is_refused_once_the_order_has_reached_delivery()
    {
        // Past inspection there is no inspection left to fail — the one cheap kind/stage guard.
        var order = InFlightOrder(WorkOrderStatus.Delivery);
        var service = ServiceFor(order, new FakeInjectedFaultRepository());

        var result = await service.Arm(order.Id, InjectedFaultKind.FailInspection, "visitor");

        Assert.Equal(ChaosArmOutcome.TargetNotInjectable, result.Outcome);
    }

    [Theory]
    [InlineData(InjectedFaultKind.TransientOnce, WorkOrderStatus.Intake)]
    [InlineData(InjectedFaultKind.TransientOnce, WorkOrderStatus.Scheduled)]
    [InlineData(InjectedFaultKind.TransientOnce, WorkOrderStatus.OnHold)]
    [InlineData(InjectedFaultKind.Poison, WorkOrderStatus.Intake)]
    [InlineData(InjectedFaultKind.Poison, WorkOrderStatus.Scheduled)]
    [InlineData(InjectedFaultKind.Poison, WorkOrderStatus.OnHold)]
    public async Task A_broker_fault_can_be_armed_up_to_and_including_the_picking_stage(
        InjectedFaultKind kind, WorkOrderStatus status)
    {
        var order = InFlightOrder(status);
        var faults = new FakeInjectedFaultRepository();
        var service = ServiceFor(order, faults);

        var result = await service.Arm(order.Id, kind, "visitor");

        Assert.Equal(ChaosArmOutcome.Armed, result.Outcome);
        Assert.True(await faults.IsArmed(order.Id, kind));
    }

    [Theory]
    [InlineData(InjectedFaultKind.TransientOnce, WorkOrderStatus.InProcess)]
    [InlineData(InjectedFaultKind.TransientOnce, WorkOrderStatus.Inspection)]
    [InlineData(InjectedFaultKind.TransientOnce, WorkOrderStatus.Delivery)]
    [InlineData(InjectedFaultKind.Poison, WorkOrderStatus.InProcess)]
    [InlineData(InjectedFaultKind.Poison, WorkOrderStatus.Inspection)]
    [InlineData(InjectedFaultKind.Poison, WorkOrderStatus.Delivery)]
    public async Task A_broker_fault_is_refused_once_the_order_has_passed_the_picking_stage(
        InjectedFaultKind kind, WorkOrderStatus status)
    {
        // The two broker faults fire on the pick (work-order.scheduled); an order already building or
        // beyond would never hit that consult point, so arming it is refused rather than left a dud.
        var order = InFlightOrder(status);
        var faults = new FakeInjectedFaultRepository();
        var service = ServiceFor(order, faults);

        var result = await service.Arm(order.Id, kind, "visitor");

        Assert.Equal(ChaosArmOutcome.TargetNotInjectable, result.Outcome);
        Assert.False(await faults.IsArmed(order.Id, kind));
    }

    // -------------------------------------------------------------------------- helpers

    private static ChaosService ServiceFor(WorkOrder order, IInjectedFaultRepository faults) =>
        new(faults, new SingleWorkOrderRepository(order), NullLogger<ChaosService>.Instance);

    /// <summary>A work order forced to a given status via the superuser command — the only way to reach one directly.</summary>
    private static WorkOrder InFlightOrder(WorkOrderStatus status)
    {
        var order = new WorkOrder("x-unit", TestData.DefaultProduct(), 1);
        order.SetStatus(status, "x-unit");
        return order;
    }

    private sealed class SingleWorkOrderRepository(WorkOrder order) : IWorkOrderRepository
    {
        public Task<WorkOrder?> Get(Guid id) => Task.FromResult<WorkOrder?>(order.Id == id ? order : null);
        public Task<WorkOrder?> GetWithHistory(Guid id) => Get(id);
        public Task<WorkOrder> Add(WorkOrder workOrder) => Task.FromResult(workOrder);
        public Task Update(WorkOrder workOrder) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkOrderListItemDto>> List(
            IReadOnlyCollection<WorkOrderStatus> statuses,
            IReadOnlyCollection<WorkOrderOrigin> origins,
            int limit)
            => Task.FromResult<IReadOnlyList<WorkOrderListItemDto>>([]);

        // 13.3's addition. Nothing in a chaos unit test reaches it; it answers like an empty
        // factory so that if anything ever does, it gets a result rather than an exception.
        public Task<IReadOnlyList<string>> ListOpenSubAssemblyRequests(
            Guid parentWorkOrderId, int parentAttemptNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class EmptyWorkOrderRepository : IWorkOrderRepository
    {
        public Task<WorkOrder?> Get(Guid id) => Task.FromResult<WorkOrder?>(null);
        public Task<WorkOrder?> GetWithHistory(Guid id) => Task.FromResult<WorkOrder?>(null);
        public Task<WorkOrder> Add(WorkOrder workOrder) => Task.FromResult(workOrder);
        public Task Update(WorkOrder workOrder) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkOrderListItemDto>> List(
            IReadOnlyCollection<WorkOrderStatus> statuses,
            IReadOnlyCollection<WorkOrderOrigin> origins,
            int limit)
            => Task.FromResult<IReadOnlyList<WorkOrderListItemDto>>([]);

        // 13.3's addition. Nothing in a chaos unit test reaches it; it answers like an empty
        // factory so that if anything ever does, it gets a result rather than an exception.
        public Task<IReadOnlyList<string>> ListOpenSubAssemblyRequests(
            Guid parentWorkOrderId, int parentAttemptNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
