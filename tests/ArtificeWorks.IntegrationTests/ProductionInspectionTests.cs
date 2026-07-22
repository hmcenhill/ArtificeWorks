using System.Diagnostics.Metrics;

using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Epic 6 against a real Postgres: production builds serialized units (6.1), inspection issues
/// per-unit verdicts (6.2), the rework cycle rebuilds only the shortfall and the cap routes to
/// Fault (6.3), and every stage is idempotent under redelivery — including concurrent
/// redelivery, which is the part only a database constraint can actually deliver (6.4).
/// </summary>
public class ProductionInspectionTests : IClassFixture<ProductionFixture>
{
    private readonly ProductionFixture _fixture;

    public ProductionInspectionTests(ProductionFixture fixture)
    {
        _fixture = fixture;
        _fixture.Verdicts.PassEverything();
    }

    // ------------------------------------------------------- 6.1 / 6.2 the unattended path

    [Fact]
    public async Task A_picked_order_builds_serialized_units_and_passes_inspection_to_delivery()
    {
        var scenario = await Seed("FLOW", orderQty: 3);

        Assert.Equal(PickOutcome.Picked, (await Pick(scenario.WorkOrderId)).Outcome);

        var production = await Produce(scenario.WorkOrderId, attempt: 1);

        Assert.Equal(ProductionOutcome.Built, production.Outcome);
        Assert.Equal(3, production.SerialNumbers!.Count);
        Assert.Equal(WorkOrderStatus.InProcess, await Status(scenario.WorkOrderId));

        // Every ordered unit exists as its own serialized row, owned by this order.
        var units = await Units(scenario.WorkOrderId);
        Assert.Equal(3, units.Count);
        Assert.All(units, unit => Assert.Equal(UnitStatus.Built, unit.Status));
        Assert.All(units, unit => Assert.Equal(1, unit.BuildAttempt));
        Assert.Equal(3, units.Select(unit => unit.SerialNumber).Distinct().Count());

        var inspection = await Inspect(scenario.WorkOrderId, attempt: 1);

        Assert.Equal(InspectionOutcome.Passed, inspection.Outcome);
        Assert.Equal(3u, inspection.UnitsPassed);
        Assert.Equal(WorkOrderStatus.Delivery, await Status(scenario.WorkOrderId));
        Assert.All(await Units(scenario.WorkOrderId), unit =>
        {
            Assert.Equal(UnitStatus.Passed, unit.Status);
            Assert.NotNull(unit.InspectedUtc);
        });

        // The whole journey is legible from the state history alone.
        var history = await History(scenario.WorkOrderId);
        Assert.Contains(history, entry => (entry.Notes ?? "").Contains("Production started"));
        Assert.Contains(history, entry => (entry.Notes ?? "").Contains("entered inspection"));
        Assert.Contains(history, entry => (entry.Notes ?? "").Contains("passed inspection"));

        // CompletedBy was silently unmapped from 4.2 until 6.1's migration — the author of a
        // transition is now actually stored, not just set in memory.
        Assert.Contains(history, entry => entry.CompletedBy == ProductionService.Author);
        Assert.Contains(history, entry => entry.CompletedBy == InspectionService.Author);
        Assert.DoesNotContain(history, entry => string.IsNullOrEmpty(entry.CompletedBy));

        var passed = Assert.Single(PublishedFor<InspectionPassed>(scenario.WorkOrderId));
        Assert.Equal(3, passed.SerialNumbers.Count);
    }

    // ----------------------------------------------------------------- 6.3 the rework cycle

    [Fact]
    public async Task A_failed_unit_is_scrapped_and_only_the_shortfall_is_rebuilt()
    {
        var scenario = await Seed("REWORK", orderQty: 3);
        await Produce(scenario.WorkOrderId, attempt: 1);

        _fixture.Verdicts.FailNext(2);
        var first = await Inspect(scenario.WorkOrderId, attempt: 1);

        Assert.Equal(InspectionOutcome.ReworkRequired, first.Outcome);
        Assert.Equal(1u, first.UnitsPassed);
        Assert.Equal(2u, first.UnitsScrapped);

        // The order really did walk backwards — the state machine's first reverse gear.
        Assert.Equal(WorkOrderStatus.InProcess, await Status(scenario.WorkOrderId));

        var rework = Assert.Single(PublishedFor<ReworkRequired>(scenario.WorkOrderId));
        Assert.Equal(2u, rework.OutstandingQty);
        Assert.Equal(1, rework.AttemptNumber);
        Assert.All(rework.Scrapped, unit => Assert.Equal("cracked mainspring", unit.Reason));

        // The rebuild builds the shortfall, not the order.
        var rebuild = await Produce(scenario.WorkOrderId, attempt: rework.AttemptNumber + 1);
        Assert.Equal(2, rebuild.SerialNumbers!.Count);

        var second = await Inspect(scenario.WorkOrderId, attempt: 2);
        Assert.Equal(InspectionOutcome.Passed, second.Outcome);
        Assert.Equal(WorkOrderStatus.Delivery, await Status(scenario.WorkOrderId));

        var units = await Units(scenario.WorkOrderId);
        Assert.Equal(5, units.Count); // 3 built + 2 rebuilt; nothing is ever deleted
        Assert.Equal(3, units.Count(unit => unit.Status == UnitStatus.Passed));
        Assert.Equal(2, units.Count(unit => unit.Status == UnitStatus.Scrapped));
        Assert.All(units.Where(unit => unit.Status == UnitStatus.Scrapped),
            unit => Assert.Equal("cracked mainspring", unit.ScrapReason));

        // The unit that passed first time round was never rebuilt or re-inspected.
        var survivor = Assert.Single(units, unit => unit.BuildAttempt == 1 && unit.Status == UnitStatus.Passed);
        Assert.NotNull(survivor.InspectedUtc);
    }

    [Fact]
    public async Task Repeated_failures_land_the_order_in_fault_and_stop_the_cycle()
    {
        var scenario = await Seed("CAPPED", orderQty: 1);
        _fixture.Verdicts.FailEverything();

        // Cap 3 = three rebuilds, so attempts 1..3 each go round again.
        for (var attempt = 1; attempt <= ProductionFixture.MaxRebuildAttempts; attempt++)
        {
            await Produce(scenario.WorkOrderId, attempt);
            var result = await Inspect(scenario.WorkOrderId, attempt);
            Assert.Equal(InspectionOutcome.ReworkRequired, result.Outcome);
        }

        // The last permitted rebuild fails too — and that is the one that would need a fourth.
        var final = ProductionFixture.MaxRebuildAttempts + 1;
        await Produce(scenario.WorkOrderId, final);
        var faulted = await Inspect(scenario.WorkOrderId, final);

        Assert.Equal(InspectionOutcome.Faulted, faulted.Outcome);
        Assert.Equal(WorkOrderStatus.Fault, await Status(scenario.WorkOrderId));

        var faultEvent = Assert.Single(PublishedFor<WorkOrderFaulted>(scenario.WorkOrderId));
        Assert.Contains($"Rebuild cap of {ProductionFixture.MaxRebuildAttempts}", faultEvent.Reason);

        // The cycle stops: three rework events, not four.
        Assert.Equal(ProductionFixture.MaxRebuildAttempts, PublishedFor<ReworkRequired>(scenario.WorkOrderId).Count);

        // And the reason is in the audit trail, naming what was scrapped.
        var faultNote = Assert.Single(await History(scenario.WorkOrderId),
            entry => entry.Status == WorkOrderStatus.Fault);
        Assert.Contains("Scrapped:", faultNote.Notes);
    }

    [Fact]
    public async Task A_faulted_order_remains_recoverable()
    {
        var scenario = await Seed("RECOVER", orderQty: 1);
        _fixture.Verdicts.FailEverything();

        for (var attempt = 1; attempt <= ProductionFixture.MaxRebuildAttempts + 1; attempt++)
        {
            await Produce(scenario.WorkOrderId, attempt);
            await Inspect(scenario.WorkOrderId, attempt);
        }

        Assert.Equal(WorkOrderStatus.Fault, await Status(scenario.WorkOrderId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ArtificeWorksDbContext>();
        var workOrder = await context.WorkOrders
            .Include(order => order.StateHistory)
            .SingleAsync(order => order.Id == scenario.WorkOrderId);

        // Fault is a stop, not a dead end: releasing restores Inspection. Nothing re-triggers
        // the pipeline from there — the same documented gap as 5.3's release-does-not-re-pick.
        Assert.True(workOrder.ReleaseHold("supervisor").Success);
        Assert.Equal(WorkOrderStatus.Inspection, workOrder.CurrentStatus);

        // ...and cancelling out of Fault still works.
        Assert.True(workOrder.Cancel("supervisor").Success);
    }

    // ------------------------------------------------------------------- 6.4 idempotency

    [Fact]
    public async Task A_redelivered_production_event_builds_once()
    {
        var scenario = await Seed("PROD-DUPE", orderQty: 2);

        var first = await Produce(scenario.WorkOrderId, attempt: 1);
        var second = await Produce(scenario.WorkOrderId, attempt: 1);

        Assert.Equal(ProductionOutcome.Built, first.Outcome);
        Assert.Equal(ProductionOutcome.AlreadyBuilt, second.Outcome);

        Assert.Equal(2, (await Units(scenario.WorkOrderId)).Count);
        Assert.Equal(1, await ProductionRunCount(scenario.WorkOrderId));

        // No second history note: a note per redelivery would itself be non-idempotent.
        Assert.Single(await History(scenario.WorkOrderId),
            entry => (entry.Notes ?? "").Contains("Production started"));

        // And no second hand-off event.
        Assert.Single(PublishedFor<ProductionCompleted>(scenario.WorkOrderId));
    }

    [Fact]
    public async Task Simultaneous_duplicate_production_deliveries_still_build_once()
    {
        // The case the pre-check cannot catch: two deliveries pass "already built?" together.
        // Only the unique index on (WorkOrderId, AttemptNumber) can separate them — and the
        // loser's units roll back with its failed insert, because they are the same write.
        var scenario = await Seed("PROD-RACE", orderQty: 4);

        var results = await Task.WhenAll(
            Task.Run(() => Produce(scenario.WorkOrderId, attempt: 1)),
            Task.Run(() => Produce(scenario.WorkOrderId, attempt: 1)));

        Assert.Equal(1, results.Count(result => result.Outcome == ProductionOutcome.Built));
        Assert.Equal(1, results.Count(result => result.Outcome == ProductionOutcome.AlreadyBuilt));

        Assert.Equal(4, (await Units(scenario.WorkOrderId)).Count);
        Assert.Equal(1, await ProductionRunCount(scenario.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProcess, await Status(scenario.WorkOrderId));
    }

    [Fact]
    public async Task A_redelivered_inspection_event_does_not_re_verdict_or_re_announce()
    {
        var scenario = await Seed("INSP-DUPE", orderQty: 2);
        await Produce(scenario.WorkOrderId, attempt: 1);

        var first = await Inspect(scenario.WorkOrderId, attempt: 1);
        var second = await Inspect(scenario.WorkOrderId, attempt: 1);

        Assert.Equal(InspectionOutcome.Passed, first.Outcome);
        Assert.Equal(InspectionOutcome.AlreadyInspected, second.Outcome);

        Assert.Equal(WorkOrderStatus.Delivery, await Status(scenario.WorkOrderId));
        Assert.Equal(1, await InspectionRunCount(scenario.WorkOrderId));

        // The order-level outcome fired exactly once — the thing the per-unit guard alone
        // could not have promised.
        Assert.Single(PublishedFor<InspectionPassed>(scenario.WorkOrderId));
        var inspectedAt = (await Units(scenario.WorkOrderId)).Select(unit => unit.InspectedUtc).ToList();
        Assert.All(inspectedAt, timestamp => Assert.NotNull(timestamp));
    }

    [Fact]
    public async Task Simultaneous_duplicate_inspection_deliveries_resolve_once()
    {
        var scenario = await Seed("INSP-RACE", orderQty: 3);
        await Produce(scenario.WorkOrderId, attempt: 1);

        var results = await Task.WhenAll(
            Task.Run(() => Inspect(scenario.WorkOrderId, attempt: 1)),
            Task.Run(() => Inspect(scenario.WorkOrderId, attempt: 1)));

        Assert.Equal(1, results.Count(result => result.Outcome == InspectionOutcome.Passed));
        Assert.Equal(1, results.Count(result => result.Outcome == InspectionOutcome.AlreadyInspected));

        Assert.Equal(1, await InspectionRunCount(scenario.WorkOrderId));
        Assert.Single(PublishedFor<InspectionPassed>(scenario.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Delivery, await Status(scenario.WorkOrderId));
    }

    [Fact]
    public async Task A_redelivery_mid_rebuild_cycle_cannot_double_advance_the_attempt_counter()
    {
        // The case Epic 5 never had: the order is legitimately going round again, so "has this
        // been produced?" has no answer. The attempt is what must happen once.
        var scenario = await Seed("CYCLE-DUPE", orderQty: 2);
        await Produce(scenario.WorkOrderId, attempt: 1);

        _fixture.Verdicts.FailNext(1);
        var inspection = await Inspect(scenario.WorkOrderId, attempt: 1);
        Assert.Equal(InspectionOutcome.ReworkRequired, inspection.Outcome);

        var rework = Assert.Single(PublishedFor<ReworkRequired>(scenario.WorkOrderId));

        // The rebuild happens...
        await Produce(scenario.WorkOrderId, attempt: rework.AttemptNumber + 1);

        // ...and then the rework event is redelivered. It must not build a third unit, and must
        // not burn another attempt.
        var replay = await Produce(scenario.WorkOrderId, attempt: rework.AttemptNumber + 1);
        Assert.Equal(ProductionOutcome.AlreadyBuilt, replay.Outcome);

        var units = await Units(scenario.WorkOrderId);
        Assert.Equal(3, units.Count); // 2 built + 1 rebuilt
        Assert.Equal(2, await ProductionRunCount(scenario.WorkOrderId));
        Assert.Equal(2, await BuildAttempt(scenario.WorkOrderId));

        // A redelivery of the *original* materials-reserved event is equally inert.
        Assert.Equal(ProductionOutcome.AlreadyBuilt, (await Produce(scenario.WorkOrderId, attempt: 1)).Outcome);
        Assert.Equal(3, (await Units(scenario.WorkOrderId)).Count);
    }

    // ------------------------------------------------------------------- 6.2 manual verdicts

    [Fact]
    public async Task A_verdict_cannot_overrule_a_unit_the_auto_inspector_already_judged()
    {
        var scenario = await Seed("MANUAL", orderQty: 2);
        await Produce(scenario.WorkOrderId, attempt: 1);
        await Inspect(scenario.WorkOrderId, attempt: 1);

        var serial = (await Units(scenario.WorkOrderId))[0].SerialNumber;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inspection = scope.ServiceProvider.GetRequiredService<InspectionService>();

        // Auto-inspection passed both units and the order has advanced to Delivery, so its
        // units are no longer up for judgement — a clean conflict, not a race to think hard
        // about. (The manual path's own state writes are covered in the unit tests.)
        var result = await inspection.RecordVerdict(
            scenario.WorkOrderId, serial, passed: false, reason: "second thoughts", "visitor");

        Assert.Equal(VerdictOutcome.NotInInspection, result.Outcome);
        Assert.Equal(UnitStatus.Passed,
            (await Units(scenario.WorkOrderId)).Single(unit => unit.SerialNumber == serial).Status);
    }

    // ------------------------------------------------------------------ 9.2 the instruments

    /// <summary>
    /// The check that catches double-counting: one order driven through the pipeline moves each
    /// transition counter by <em>exactly</em> one.
    /// <para>
    /// This is the failure mode worth guarding, because "count it where it happens" is easy to
    /// satisfy twice — once in the service that commits the transition and once in the handler
    /// that called it — and a throughput graph that is quietly double every real number is worse
    /// than no graph.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Driving_an_order_end_to_end_moves_each_transition_counter_exactly_once()
    {
        var meterFactory = _fixture.Services.GetRequiredService<IMeterFactory>();

        using var transitions = new MetricCollector<long>(
            meterFactory, ArtificeWorksMetrics.MeterName, "artificeworks.work_orders.transitions");
        using var unitsBuilt = new MetricCollector<long>(
            meterFactory, ArtificeWorksMetrics.MeterName, "artificeworks.units.built");
        using var unitsPassed = new MetricCollector<long>(
            meterFactory, ArtificeWorksMetrics.MeterName, "artificeworks.units.passed");

        var scenario = await Seed("METRICS", orderQty: 2);

        await Pick(scenario.WorkOrderId);
        await Produce(scenario.WorkOrderId, attempt: 1);
        await Inspect(scenario.WorkOrderId, attempt: 1);

        // Scheduled → InProcess (production) and InProcess → Inspection → Delivery (inspection).
        // Picking transitions nothing on the happy path, which is why it contributes none.
        Assert.Equal(1, CountOf(transitions, "Scheduled", "InProcess"));
        Assert.Equal(1, CountOf(transitions, "InProcess", "Inspection"));
        Assert.Equal(1, CountOf(transitions, "Inspection", "Delivery"));

        Assert.Equal(2, unitsBuilt.GetMeasurementSnapshot().Sum(m => m.Value));
        Assert.Equal(2, unitsPassed.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    private static long CountOf(MetricCollector<long> collector, string from, string to) =>
        collector.GetMeasurementSnapshot()
            .Where(measurement =>
                measurement.Tags.TryGetValue("from", out var f) && (string?)f == from &&
                measurement.Tags.TryGetValue("to", out var t) && (string?)t == to)
            .Sum(measurement => measurement.Value);

    // ------------------------------------------------------------------------- helpers

    private async Task<PickResult> Pick(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MaterialPickingService>().PickMaterials(workOrderId);
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

    private IReadOnlyList<T> PublishedFor<T>(Guid workOrderId) where T : Application.Messaging.IntegrationEvent
        => _fixture.Published.OfType<T>()
            .Where(@event => WorkOrderIdOf(@event) == workOrderId)
            .ToList();

    private static Guid WorkOrderIdOf(Application.Messaging.IntegrationEvent @event) => @event switch
    {
        ProductionCompleted e => e.WorkOrderId,
        InspectionPassed e => e.WorkOrderId,
        ReworkRequired e => e.WorkOrderId,
        WorkOrderFaulted e => e.WorkOrderId,
        MaterialsReserved e => e.WorkOrderId,
        _ => Guid.Empty
    };

    private async Task<WorkOrderStatus> Status(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return (await context.WorkOrders.AsNoTracking().SingleAsync(order => order.Id == workOrderId)).CurrentStatus;
    }

    private async Task<int> BuildAttempt(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return (await context.WorkOrders.AsNoTracking().SingleAsync(order => order.Id == workOrderId)).BuildAttempt;
    }

    private async Task<List<StockKeepingUnit>> Units(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.StockKeepingUnits
            .AsNoTracking()
            .Where(unit => EF.Property<Guid>(unit, "work_order_id") == workOrderId)
            .OrderBy(unit => unit.BuildAttempt)
            .ThenBy(unit => unit.BuiltUtc)
            .ToListAsync();
    }

    private async Task<int> ProductionRunCount(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.ProductionRuns.CountAsync(run => run.WorkOrderId == workOrderId);
    }

    private async Task<int> InspectionRunCount(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.InspectionRuns.CountAsync(run => run.WorkOrderId == workOrderId);
    }

    private async Task<List<WorkOrderStateHistory>> History(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.OrderStateHistory
            .AsNoTracking()
            .Where(entry => entry.WorkOrderId == workOrderId)
            .OrderBy(entry => entry.ChangedUtc)
            .ToListAsync();
    }

    /// <summary>An isolated product, stocked components, and one scheduled work order.</summary>
    private async Task<Scenario> Seed(string tag, uint orderQty)
    {
        await using var context = _fixture.NewContext();

        var product = new Product($"PRD-{tag}", $"{tag} Automaton");
        var chassis = new Component($"CMP-{tag}-CHASSIS", "Chassis", onHand: 100);
        product.AddBomLine(chassis, qtyPerUnit: 1);

        var workOrder = new WorkOrder("seed", product, orderQty);
        workOrder.AdvanceToNextStep("seed"); // Intake -> Scheduled

        context.Products.Add(product);
        context.Components.Add(chassis);
        context.WorkOrders.Add(workOrder);
        await context.SaveChangesAsync();

        return new Scenario(workOrder.Id);
    }

    private sealed record Scenario(Guid WorkOrderId);
}
