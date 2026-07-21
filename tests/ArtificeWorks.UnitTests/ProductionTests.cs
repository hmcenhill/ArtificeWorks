using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// The domain rules Epic 6 adds: building serialized units, the backward transition that makes
/// the rework loop a cycle, the derived quantities both of those read, and the guarded Fault.
/// </summary>
public class ProductionTests
{
    private const string Author = "x-unit";

    private static WorkOrder ScheduledOrder(uint qty = 3)
    {
        var workOrder = new WorkOrder(Author, TestData.DefaultProduct(), qty);
        workOrder.AdvanceToNextStep(Author); // Intake -> Scheduled
        return workOrder;
    }

    // ------------------------------------------------------------------------ 6.1 building

    [Fact]
    public void Building_starts_production_and_serializes_the_whole_ordered_quantity()
    {
        var workOrder = ScheduledOrder(qty: 3);

        var result = workOrder.Build(Author, attemptNumber: 1);

        Assert.True(result.Success);
        Assert.Equal(WorkOrderStatus.InProcess, workOrder.CurrentStatus);
        Assert.Equal(3, workOrder.AssignedStock.Count);
        Assert.All(workOrder.AssignedStock, unit => Assert.Equal(UnitStatus.Built, unit.Status));
        Assert.All(workOrder.AssignedStock, unit => Assert.Equal(1, unit.BuildAttempt));

        // Every unit is individually addressable — the point of serializing them.
        Assert.Equal(3, workOrder.AssignedStock.Select(unit => unit.SerialNumber).Distinct().Count());
    }

    [Fact]
    public void Building_is_visible_in_the_state_history()
    {
        var workOrder = ScheduledOrder();
        var before = workOrder.StateHistory.Count;

        workOrder.Build(Author, attemptNumber: 1, notes: "Production started: building 3 unit(s).");

        Assert.Equal(before + 1, workOrder.StateHistory.Count);
        var entry = workOrder.StateHistory.Last();
        Assert.Equal(WorkOrderStatus.InProcess, entry.Status);
        Assert.Contains("building 3", entry.Notes);
    }

    [Theory]
    [InlineData(WorkOrderStatus.OnHold, TransitionErrorCode.MustReleaseFirst)]
    [InlineData(WorkOrderStatus.Fault, TransitionErrorCode.MustReleaseFirst)]
    [InlineData(WorkOrderStatus.Completed, TransitionErrorCode.TerminalState)]
    [InlineData(WorkOrderStatus.Cancelled, TransitionErrorCode.TerminalState)]
    [InlineData(WorkOrderStatus.Intake, TransitionErrorCode.InvalidTransition)]
    [InlineData(WorkOrderStatus.Delivery, TransitionErrorCode.InvalidTransition)]
    public void Building_is_refused_from_states_that_are_not_ready_to_produce(
        WorkOrderStatus status, TransitionErrorCode expected)
    {
        var workOrder = new WorkOrder(Author, TestData.DefaultProduct(), 2);
        workOrder.SetStatus(status, Author);

        var result = workOrder.Build(Author, attemptNumber: 1);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Code);
        Assert.Empty(workOrder.AssignedStock);
    }

    [Theory]
    [InlineData(2)] // skips attempt 1
    [InlineData(0)]
    public void An_attempt_out_of_sequence_builds_nothing(int attemptNumber)
    {
        var workOrder = ScheduledOrder();

        var result = workOrder.Build(Author, attemptNumber);

        Assert.False(result.Success);
        Assert.Equal(TransitionErrorCode.AttemptOutOfSequence, result.Code);
        Assert.Empty(workOrder.AssignedStock);
        Assert.Equal(0, workOrder.BuildAttempt);
    }

    [Fact]
    public void Replaying_the_same_attempt_number_builds_nothing()
    {
        // The domain's cheap guard in front of the database constraint: a redelivered event
        // asks for an attempt that has already happened.
        var workOrder = ScheduledOrder(qty: 2);
        workOrder.Build(Author, attemptNumber: 1);

        var replay = workOrder.Build(Author, attemptNumber: 1);

        Assert.False(replay.Success);
        Assert.Equal(TransitionErrorCode.AttemptOutOfSequence, replay.Code);
        Assert.Equal(2, workOrder.AssignedStock.Count);
    }

    [Fact]
    public void Nothing_is_built_when_the_order_is_already_accounted_for()
    {
        var workOrder = ScheduledOrder(qty: 2);
        workOrder.Build(Author, attemptNumber: 1);
        workOrder.AdvanceToNextStep(Author); // -> Inspection
        foreach (var unit in workOrder.AssignedStock) { unit.Pass(); }
        workOrder.SetStatus(WorkOrderStatus.InProcess, Author);

        var result = workOrder.Build(Author, attemptNumber: 2);

        Assert.False(result.Success);
        Assert.Equal(TransitionErrorCode.InvalidTransition, result.Code);
    }

    // ------------------------------------------------------------- 6.3 derived quantities

    [Fact]
    public void Scrapped_units_do_not_count_towards_the_order_but_passed_ones_do()
    {
        var workOrder = ScheduledOrder(qty: 5);
        workOrder.Build(Author, attemptNumber: 1);

        var units = workOrder.AssignedStock.ToList();
        units[0].Pass();
        units[1].Pass();
        units[2].Scrap("cracked mainspring");
        units[3].Scrap("mis-cut escapement");
        // units[4] still awaiting a verdict

        Assert.Equal(2u, workOrder.PassedQty);
        Assert.Equal(3u, workOrder.LiveQty);      // 2 passed + 1 still built
        Assert.Equal(2u, workOrder.OutstandingQty);
        Assert.False(workOrder.IsFulfilled);
    }

    [Fact]
    public void A_rebuild_builds_only_the_shortfall_and_leaves_passing_units_alone()
    {
        var workOrder = ScheduledOrder(qty: 4);
        workOrder.Build(Author, attemptNumber: 1);

        var firstAttempt = workOrder.AssignedStock.ToList();
        firstAttempt[0].Pass();
        firstAttempt[1].Pass();
        firstAttempt[2].Scrap("cracked mainspring");
        firstAttempt[3].Scrap("mis-cut escapement");

        workOrder.SetStatus(WorkOrderStatus.Inspection, Author);
        Assert.True(workOrder.ReturnToProduction(Author).Success);

        var rebuild = workOrder.Build(Author, attemptNumber: 2);

        Assert.True(rebuild.Success);
        Assert.Equal(2, workOrder.AssignedStock.Count(unit => unit.BuildAttempt == 2));
        Assert.Equal(6, workOrder.AssignedStock.Count); // 4 + 2, nothing deleted

        // The passing units from attempt 1 are untouched — never rebuilt, never re-inspected.
        Assert.Equal(2u, workOrder.PassedQty);
        Assert.Equal(2, workOrder.AssignedStock.Count(unit => unit.Status == UnitStatus.Built));
        Assert.Equal(0u, workOrder.OutstandingQty);
    }

    [Fact]
    public void The_order_is_fulfilled_only_at_the_full_passing_quantity()
    {
        var workOrder = ScheduledOrder(qty: 2);
        workOrder.Build(Author, attemptNumber: 1);

        workOrder.AssignedStock[0].Pass();
        Assert.False(workOrder.IsFulfilled);

        workOrder.AssignedStock[1].Pass();
        Assert.True(workOrder.IsFulfilled);
    }

    // -------------------------------------------------------- 6.3 the backward transition

    [Fact]
    public void Returning_to_production_walks_the_state_machine_backwards()
    {
        var workOrder = ScheduledOrder();
        workOrder.SetStatus(WorkOrderStatus.Inspection, Author);

        var result = workOrder.ReturnToProduction(Author, "2 unit(s) short");

        Assert.True(result.Success);
        Assert.Equal(WorkOrderStatus.InProcess, workOrder.CurrentStatus);
        Assert.Equal("2 unit(s) short", workOrder.StateHistory.Last().Notes);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Intake)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProcess)]
    [InlineData(WorkOrderStatus.Delivery)]
    [InlineData(WorkOrderStatus.OnHold)]
    public void Only_an_order_in_inspection_can_be_returned_to_production(WorkOrderStatus status)
    {
        var workOrder = ScheduledOrder();
        workOrder.SetStatus(status, Author);

        var result = workOrder.ReturnToProduction(Author);

        Assert.False(result.Success);
        Assert.Equal(TransitionErrorCode.InvalidTransition, result.Code);
        Assert.Equal(status, workOrder.CurrentStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public void A_terminal_order_is_never_returned_to_production(WorkOrderStatus status)
    {
        var workOrder = ScheduledOrder();
        workOrder.SetStatus(status, Author);

        var result = workOrder.ReturnToProduction(Author);

        Assert.False(result.Success);
        Assert.Equal(TransitionErrorCode.TerminalState, result.Code);
    }

    // -------------------------------------------------------------------- 6.3 the fault verb

    [Fact]
    public void Faulting_records_the_reason_and_stays_recoverable()
    {
        var workOrder = ScheduledOrder();
        workOrder.SetStatus(WorkOrderStatus.Inspection, Author);

        var result = workOrder.Fault(Author, "Rebuild cap of 3 exceeded.");

        Assert.True(result.Success);
        Assert.Equal(WorkOrderStatus.Fault, workOrder.CurrentStatus);
        Assert.Equal("Rebuild cap of 3 exceeded.", workOrder.StateHistory.Last().Notes);

        // Fault is deliberately non-terminal: it is a stop, not a dead end.
        Assert.True(workOrder.ReleaseHold(Author).Success);
        Assert.Equal(WorkOrderStatus.Inspection, workOrder.CurrentStatus);
    }

    [Fact]
    public void A_faulted_order_can_still_be_cancelled()
    {
        var workOrder = ScheduledOrder();
        workOrder.Fault(Author, "gave up");

        Assert.True(workOrder.Cancel(Author).Success);
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.CurrentStatus);
    }

    [Fact]
    public void A_fault_must_carry_a_reason()
    {
        var workOrder = ScheduledOrder();

        var result = workOrder.Fault(Author, "  ");

        Assert.False(result.Success);
        Assert.Equal(TransitionErrorCode.InvalidTransition, result.Code);
        Assert.Equal(WorkOrderStatus.Scheduled, workOrder.CurrentStatus);
    }

    [Fact]
    public void An_order_cannot_be_faulted_twice()
    {
        var workOrder = ScheduledOrder();
        workOrder.Fault(Author, "first");

        var again = workOrder.Fault(Author, "second");

        Assert.False(again.Success);
        Assert.Equal(TransitionErrorCode.AlreadyHeld, again.Code);
    }

    // ---------------------------------------------------------------- 6.2 per-unit verdicts

    [Fact]
    public void A_unit_records_its_verdict_and_when_it_was_made()
    {
        var unit = new StockKeepingUnit(TestData.DefaultProduct());
        Assert.Null(unit.InspectedUtc);

        Assert.True(unit.Pass().Success);

        Assert.Equal(UnitStatus.Passed, unit.Status);
        Assert.NotNull(unit.InspectedUtc);
        Assert.Null(unit.ScrapReason);
    }

    [Fact]
    public void A_scrapped_unit_carries_its_reason()
    {
        var unit = new StockKeepingUnit(TestData.DefaultProduct());

        Assert.True(unit.Scrap("cracked mainspring").Success);

        Assert.Equal(UnitStatus.Scrapped, unit.Status);
        Assert.Equal("cracked mainspring", unit.ScrapReason);
    }

    [Fact]
    public void A_scrapped_unit_must_carry_a_reason()
    {
        var unit = new StockKeepingUnit(TestData.DefaultProduct());

        var result = unit.Scrap("   ");

        Assert.False(result.Success);
        Assert.Equal(UnitStatus.Built, unit.Status);
    }

    [Fact]
    public void A_unit_cannot_be_inspected_twice()
    {
        // The guard that resolves an auto-inspector / human race, and that stops a redelivered
        // batch from re-verdicting units it already judged.
        var unit = new StockKeepingUnit(TestData.DefaultProduct());
        unit.Pass();

        var second = unit.Scrap("changed my mind");

        Assert.False(second.Success);
        Assert.Equal(TransitionErrorCode.AlreadyInspected, second.Code);
        Assert.Equal(UnitStatus.Passed, unit.Status);
        Assert.Null(unit.ScrapReason);
    }

    [Fact]
    public void The_fulfilment_guard_counts_live_units_not_rows()
    {
        // Regression guard for the 6.1 fix: AssignSku used to count every row it had ever been
        // given, which would have made rebuilding a shortfall impossible.
        var workOrder = ScheduledOrder(qty: 2);
        workOrder.Build(Author, attemptNumber: 1);
        workOrder.AssignedStock[0].Scrap("cracked mainspring");

        Assert.True(workOrder.AssignSku(new StockKeepingUnit(TestData.DefaultProduct(), 2)));
        Assert.False(workOrder.AssignSku(new StockKeepingUnit(TestData.DefaultProduct(), 2)));
        Assert.Equal(3, workOrder.AssignedStock.Count);
    }

    [Fact]
    public void Units_awaiting_inspection_are_scoped_to_one_attempt()
    {
        var workOrder = ScheduledOrder(qty: 2);
        workOrder.Build(Author, attemptNumber: 1);
        workOrder.AssignedStock[0].Pass();
        workOrder.AssignedStock[1].Scrap("cracked mainspring");
        workOrder.SetStatus(WorkOrderStatus.Inspection, Author);
        workOrder.ReturnToProduction(Author);
        workOrder.Build(Author, attemptNumber: 2);

        Assert.Empty(workOrder.UnitsAwaitingInspection(1));
        Assert.Single(workOrder.UnitsAwaitingInspection(2));
    }
}
