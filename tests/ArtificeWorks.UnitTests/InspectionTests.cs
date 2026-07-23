using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Production;

using Microsoft.Extensions.Logging.Abstractions;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// The inspection workflow's decisions, driven against in-memory fakes so the rework cycle and
/// the rebuild cap can be walked end to end without a database.
/// <para>
/// The <em>guarantees</em> that rest on database constraints — that a redelivered attempt writes
/// nothing — cannot be proved here and are not attempted; those live in the integration tests
/// against real Postgres. What these cover is the reasoning: who passes, who fails, what the
/// order does next, and exactly where the cap bites.
/// </para>
/// </summary>
public class InspectionTests
{
    private const string Author = "x-unit";

    // -------------------------------------------------------------------- verdict sources

    [Fact]
    public void The_default_failure_rate_passes_everything()
    {
        // The setting that lets the factory run unattended: FailureRate defaults to 0.0.
        var source = new RandomVerdictSource(new InspectionConfiguration());

        var verdicts = Enumerable.Range(0, 200)
            .Select(_ => source.Verdict(new StockKeepingUnit(TestData.DefaultProduct())))
            .ToList();

        Assert.All(verdicts, verdict => Assert.True(verdict.Passed));
    }

    [Fact]
    public void A_configured_failure_rate_is_honoured_and_reproducible_under_a_seed()
    {
        var config = new InspectionConfiguration { FailureRate = 0.5, Seed = 1234 };

        var first = Roll(new RandomVerdictSource(config), 500);
        var second = Roll(new RandomVerdictSource(config), 500);

        // Same seed, same sequence — which is the only way to assert on a coin flip.
        Assert.Equal(first, second);

        // And the rate is roughly what was asked for, not "sometimes".
        var failures = first.Count(passed => !passed);
        Assert.InRange(failures, 200, 300);

        static List<bool> Roll(IVerdictSource source, int count) =>
            Enumerable.Range(0, count)
                .Select(_ => source.Verdict(new StockKeepingUnit(TestData.DefaultProduct())).Passed)
                .ToList();
    }

    [Fact]
    public void A_failing_verdict_carries_the_configured_reason()
    {
        var config = new InspectionConfiguration { FailureRate = 1.0, AutoFailureReason = "escapement out of beat" };

        var verdict = new RandomVerdictSource(config).Verdict(new StockKeepingUnit(TestData.DefaultProduct()));

        Assert.False(verdict.Passed);
        Assert.Equal("escapement out of beat", verdict.Reason);
    }

    // ------------------------------------------------------------------- the all-pass path

    [Fact]
    public async Task Everything_passing_advances_the_order_to_delivery_and_announces_it()
    {
        var harness = new Harness(qty: 2, failEverything: false);
        await harness.Produce(attempt: 1);

        var result = await harness.Inspect(attempt: 1);

        Assert.Equal(InspectionOutcome.Passed, result.Outcome);
        Assert.Equal(2u, result.UnitsPassed);
        Assert.Equal(WorkOrderStatus.Delivery, harness.WorkOrder.CurrentStatus);

        var passed = Assert.Single(harness.Published.OfType<InspectionPassed>());
        Assert.Equal(2, passed.SerialNumbers.Count);
        Assert.Empty(harness.Published.OfType<ReworkRequired>());
    }

    // ------------------------------------------------------------------- the rework cycle

    [Fact]
    public async Task A_shortfall_sends_the_order_back_to_production_for_the_shortfall_only()
    {
        var harness = new Harness(qty: 3, failEverything: false);
        await harness.Produce(attempt: 1);
        harness.FailNext(count: 2);

        var result = await harness.Inspect(attempt: 1);

        Assert.Equal(InspectionOutcome.ReworkRequired, result.Outcome);
        Assert.Equal(WorkOrderStatus.InProcess, harness.WorkOrder.CurrentStatus);

        var rework = Assert.Single(harness.Published.OfType<ReworkRequired>());
        Assert.Equal(2u, rework.OutstandingQty);
        Assert.Equal(1, rework.AttemptNumber);
        Assert.Equal(2, rework.Scrapped.Count);
        Assert.All(rework.Scrapped, unit => Assert.False(string.IsNullOrWhiteSpace(unit.Reason)));

        // The rebuild really does build only the shortfall.
        await harness.Produce(attempt: 2);
        Assert.Equal(2, harness.WorkOrder.AssignedStock.Count(unit => unit.BuildAttempt == 2));

        var second = await harness.Inspect(attempt: 2);
        Assert.Equal(InspectionOutcome.Passed, second.Outcome);
        Assert.Equal(WorkOrderStatus.Delivery, harness.WorkOrder.CurrentStatus);
        Assert.Equal(3u, harness.WorkOrder.PassedQty);

        // The unit that passed on attempt 1 was never rebuilt or re-inspected.
        Assert.Single(harness.WorkOrder.AssignedStock,
            unit => unit.BuildAttempt == 1 && unit.Status == UnitStatus.Passed);
    }

    // ------------------------------------------------------------------- the cap boundary

    [Fact]
    public async Task The_last_permitted_rebuild_still_gets_a_rework_event()
    {
        // Cap 3 means three rebuilds, so attempts 1..3 all end in "go round again".
        var harness = new Harness(qty: 1, failEverything: true, maxRebuildAttempts: 3);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await harness.Produce(attempt);
            var result = await harness.Inspect(attempt);

            Assert.Equal(InspectionOutcome.ReworkRequired, result.Outcome);
            Assert.Equal(WorkOrderStatus.InProcess, harness.WorkOrder.CurrentStatus);
        }

        Assert.Equal(3, harness.Published.OfType<ReworkRequired>().Count);
        Assert.Empty(harness.Published.OfType<WorkOrderFaulted>());
    }

    [Fact]
    public async Task Exceeding_the_cap_faults_the_order_and_stops_the_cycle()
    {
        var harness = new Harness(qty: 1, failEverything: true, maxRebuildAttempts: 3);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await harness.Produce(attempt);
            await harness.Inspect(attempt);
        }

        // The fourth attempt is the third rebuild — allowed. Its failure is the one that
        // would need a fourth rebuild, and that is where the cap bites.
        await harness.Produce(attempt: 4);
        var result = await harness.Inspect(attempt: 4);

        Assert.Equal(InspectionOutcome.Faulted, result.Outcome);
        Assert.Equal(WorkOrderStatus.Fault, harness.WorkOrder.CurrentStatus);

        var faulted = Assert.Single(harness.Published.OfType<WorkOrderFaulted>());
        Assert.Contains("Rebuild cap of 3", faulted.Reason);
        Assert.Equal(4, faulted.AttemptNumber);

        // The cycle stops: no fourth rework event to send it round again.
        Assert.Equal(3, harness.Published.OfType<ReworkRequired>().Count);

        // The reason names what was scrapped — the trail explains the cycle without the logs.
        var scrapped = harness.WorkOrder.AssignedStock.First(unit => unit.Status == UnitStatus.Scrapped);
        Assert.Contains(scrapped.SerialNumber.ToString(), faulted.Reason);
    }

    [Fact]
    public async Task A_faulted_order_stays_recoverable()
    {
        var harness = new Harness(qty: 1, failEverything: true, maxRebuildAttempts: 1);
        await harness.Produce(attempt: 1);
        await harness.Inspect(attempt: 1);
        await harness.Produce(attempt: 2);
        await harness.Inspect(attempt: 2);

        Assert.Equal(WorkOrderStatus.Fault, harness.WorkOrder.CurrentStatus);

        // Releasing restores the status it was in — Inspection. Nothing re-triggers the
        // pipeline from there; that is documented, not an oversight (see HANDOFF).
        Assert.True(harness.WorkOrder.ReleaseHold("supervisor").Success);
        Assert.Equal(WorkOrderStatus.Inspection, harness.WorkOrder.CurrentStatus);
    }

    // ---------------------------------------------------------------- manual verdicts (6.2)

    [Fact]
    public async Task With_auto_inspection_off_units_wait_for_a_human()
    {
        var harness = new Harness(qty: 2, failEverything: false, autoInspect: false);
        await harness.Produce(attempt: 1);

        var result = await harness.Inspect(attempt: 1);

        Assert.Equal(InspectionOutcome.AwaitingVerdicts, result.Outcome);
        Assert.Equal(WorkOrderStatus.Inspection, harness.WorkOrder.CurrentStatus);
        Assert.Empty(harness.Published.OfType<InspectionPassed>());
        Assert.All(harness.WorkOrder.AssignedStock, unit => Assert.Equal(UnitStatus.Built, unit.Status));
    }

    [Fact]
    public async Task A_manual_verdict_reaches_the_same_state_the_auto_inspector_would()
    {
        var harness = new Harness(qty: 2, failEverything: false, autoInspect: false);
        await harness.Produce(attempt: 1);
        await harness.Inspect(attempt: 1);

        var serials = harness.WorkOrder.AssignedStock.Select(unit => unit.SerialNumber).ToList();

        var first = await harness.Verdict(serials[0], passed: true);
        Assert.Equal(VerdictOutcome.Recorded, first.Outcome);
        Assert.Equal(InspectionOutcome.AwaitingVerdicts, first.OrderOutcome);
        Assert.Empty(harness.Published.OfType<InspectionPassed>());

        // The verdict that completes the attempt resolves the order, exactly as the automatic
        // path does — same transition, same event.
        var second = await harness.Verdict(serials[1], passed: true);
        Assert.Equal(InspectionOutcome.Passed, second.OrderOutcome);
        Assert.Equal(WorkOrderStatus.Delivery, harness.WorkOrder.CurrentStatus);
        Assert.Single(harness.Published.OfType<InspectionPassed>());
    }

    [Fact]
    public async Task A_manual_failure_drives_the_rework_loop_too()
    {
        var harness = new Harness(qty: 1, failEverything: false, autoInspect: false);
        await harness.Produce(attempt: 1);
        await harness.Inspect(attempt: 1);

        var result = await harness.Verdict(
            harness.WorkOrder.AssignedStock[0].SerialNumber, passed: false, reason: "bent gear train");

        Assert.Equal(InspectionOutcome.ReworkRequired, result.OrderOutcome);
        Assert.Equal(WorkOrderStatus.InProcess, harness.WorkOrder.CurrentStatus);
        var rework = Assert.Single(harness.Published.OfType<ReworkRequired>());
        Assert.Equal("bent gear train", rework.Scrapped.Single().Reason);
    }

    [Fact]
    public async Task A_unit_cannot_be_verdicted_twice_through_the_api()
    {
        var harness = new Harness(qty: 2, failEverything: false, autoInspect: false);
        await harness.Produce(attempt: 1);
        await harness.Inspect(attempt: 1);
        var serial = harness.WorkOrder.AssignedStock[0].SerialNumber;

        await harness.Verdict(serial, passed: true);
        var again = await harness.Verdict(serial, passed: false, reason: "changed my mind");

        Assert.Equal(VerdictOutcome.AlreadyInspected, again.Outcome);
    }

    [Fact]
    public async Task Verdicts_are_refused_for_unknown_units_and_orders_that_are_not_in_inspection()
    {
        var harness = new Harness(qty: 1, failEverything: false, autoInspect: false);
        await harness.Produce(attempt: 1);

        // Still InProcess: its units are not up for judgement yet.
        var tooEarly = await harness.Verdict(harness.WorkOrder.AssignedStock[0].SerialNumber, passed: true);
        Assert.Equal(VerdictOutcome.NotInInspection, tooEarly.Outcome);

        await harness.Inspect(attempt: 1);

        var unknown = await harness.Verdict(Guid.NewGuid(), passed: true);
        Assert.Equal(VerdictOutcome.UnitNotFound, unknown.Outcome);

        var noReason = await harness.Verdict(harness.WorkOrder.AssignedStock[0].SerialNumber, passed: false);
        Assert.Equal(VerdictOutcome.ReasonRequired, noReason.Outcome);
    }

    // ------------------------------------------------------------------------------ harness

    /// <summary>
    /// One work order, the two services, and in-memory repositories — enough to walk the whole
    /// rework cycle. The fake run repositories model the unique constraint as a set, which is
    /// exactly as much of the database as the workflow's <em>decisions</em> depend on.
    /// </summary>
    private sealed class Harness
    {
        public WorkOrder WorkOrder { get; }
        public RecordingPublisher Published { get; } = new();

        private readonly ProductionService _production;
        private readonly InspectionService _inspection;
        private readonly ScriptedVerdictSource _verdicts;

        public Harness(uint qty, bool failEverything, int maxRebuildAttempts = 3, bool autoInspect = true)
        {
            WorkOrder = new WorkOrder(Author, TestData.DefaultProduct(), qty);
            WorkOrder.AdvanceToNextStep(Author); // Intake -> Scheduled

            var orders = new SingleWorkOrderRepository(WorkOrder);
            _verdicts = new ScriptedVerdictSource(failEverything);

            var metrics = TestData.Metrics();

            _production = new ProductionService(
                orders, new FakeProductionRunRepository(), Published, metrics,
                NullLogger<ProductionService>.Instance);

            _inspection = new InspectionService(
                orders, new FakeInspectionRunRepository(), _verdicts, Published,
                new InspectionConfiguration { AutoInspect = autoInspect },
                new ProductionConfiguration { MaxRebuildAttempts = maxRebuildAttempts },
                metrics,
                NullLogger<InspectionService>.Instance);
        }

        public Task<ProductionResult> Produce(int attempt) => _production.Produce(WorkOrder.Id, attempt);

        public Task<InspectionResult> Inspect(int attempt) => _inspection.InspectAttempt(WorkOrder.Id, attempt);

        public Task<VerdictResult> Verdict(Guid serialNumber, bool passed, string? reason = null) =>
            _inspection.RecordVerdict(WorkOrder.Id, serialNumber, passed, reason, "visitor");

        /// <summary>Fails the next <paramref name="count"/> units the inspector looks at.</summary>
        public void FailNext(int count) => _verdicts.FailNext(count);
    }

    private sealed class ScriptedVerdictSource(bool failEverything) : IVerdictSource
    {
        private int _failuresRemaining;

        public void FailNext(int count) => _failuresRemaining = count;

        public UnitVerdict Verdict(StockKeepingUnit unit)
        {
            if (failEverything || _failuresRemaining > 0)
            {
                _failuresRemaining--;
                return new UnitVerdict(false, "cracked mainspring");
            }
            return new UnitVerdict(true);
        }
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

    private sealed class FakeProductionRunRepository : IProductionRunRepository
    {
        private readonly HashSet<(Guid, int)> _committed = [];

        public Task<bool> AttemptExists(Guid workOrderId, int attemptNumber, CancellationToken cancellationToken = default)
            => Task.FromResult(_committed.Contains((workOrderId, attemptNumber)));

        public Task<bool> TryCommitAttempt(ProductionRun run, CancellationToken cancellationToken = default)
            => Task.FromResult(_committed.Add((run.WorkOrderId, run.AttemptNumber)));
    }

    private sealed class FakeInspectionRunRepository : IInspectionRunRepository
    {
        private readonly HashSet<(Guid, int)> _committed = [];

        public Task<bool> AttemptExists(Guid workOrderId, int attemptNumber, CancellationToken cancellationToken = default)
            => Task.FromResult(_committed.Contains((workOrderId, attemptNumber)));

        public Task<bool> TryCommitInspection(InspectionRun run, CancellationToken cancellationToken = default)
            => Task.FromResult(_committed.Add((run.WorkOrderId, run.AttemptNumber)));
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
