using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Handlers;
using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Shipping;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Epic 7 against a real Postgres: a passed order gets a parcel (7.1), dispatch completes it
/// (7.2), a refusing carrier holds it recoverably and a release re-requests the booking (7.3),
/// and the timeline narrates all of it (7.4).
/// </summary>
public class ShippingWorkflowTests : IClassFixture<ShippingFixture>
{
    private readonly ShippingFixture _fixture;

    public ShippingWorkflowTests(ShippingFixture fixture)
    {
        _fixture = fixture;
        _fixture.Verdicts.PassEverything();
        _fixture.Carriers.AcceptEverything();
        _fixture.ShippingConfig.AutoBook = true;
    }

    // ------------------------------------------------------------------------ 7.1 booking

    [Fact]
    public async Task A_passed_order_gets_a_parcel_naming_a_carrier_a_tracking_number_and_its_units()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-OK", qty: 2);

        var result = await Book(workOrderId, serials);

        Assert.Equal(BookingOutcome.Booked, result.Outcome);

        var shipment = await ShipmentFor(workOrderId);
        Assert.NotNull(shipment);
        Assert.Equal(ShipmentStatus.Booked, shipment!.Status);
        Assert.False(string.IsNullOrWhiteSpace(shipment.Carrier));
        Assert.False(string.IsNullOrWhiteSpace(shipment.TrackingNumber));
        Assert.True(shipment.EstimatedArrivalUtc > shipment.BookedUtc);
        Assert.Null(shipment.DispatchedUtc);

        // Exactly the passed units, nothing else.
        Assert.Equal(
            serials.OrderBy(s => s),
            shipment.Lines.Select(line => line.SerialNumber).OrderBy(s => s));

        // Booking transitions nothing: the order was already in Delivery, and the parcel's own
        // progress lives on the shipment. Two clocks on one aggregate would be the easy mistake.
        Assert.Equal(WorkOrderStatus.Delivery, await Status(workOrderId));

        Assert.Contains(await History(workOrderId), entry => (entry.Notes ?? "").Contains("Shipment booked"));

        var announced = Assert.Single(PublishedFor<ShipmentScheduled>(workOrderId));
        Assert.Equal(shipment.TrackingNumber, announced.TrackingNumber);
        Assert.Equal(2, announced.SerialNumbers.Count);
    }

    [Fact]
    public async Task Only_the_units_that_passed_ever_ship()
    {
        var scenario = await Seed("SHIP-SCRAP", qty: 2);
        await Pick(scenario);
        await Produce(scenario, attempt: 1);

        // One unit fails, the shortfall is rebuilt, and the order reaches Delivery with three
        // units on file — two live, one scrapped.
        _fixture.Verdicts.FailNext(1);
        await Inspect(scenario, attempt: 1);
        _fixture.Verdicts.PassEverything();
        await Produce(scenario, attempt: 2);
        await Inspect(scenario, attempt: 2);

        Assert.Equal(WorkOrderStatus.Delivery, await Status(scenario));

        // Book from the order rather than from an event payload, which is the API path.
        Assert.Equal(BookingOutcome.Booked, (await BookByHand(scenario, carrier: null)).Outcome);

        var units = await Units(scenario);
        Assert.Equal(3, units.Count);

        var shipment = (await ShipmentFor(scenario))!;
        var shipped = shipment.Lines.Select(line => line.SerialNumber).ToHashSet();

        Assert.Equal(2, shipped.Count);
        Assert.DoesNotContain(units.Single(unit => unit.Status == UnitStatus.Scrapped).SerialNumber, shipped);
    }

    [Fact]
    public async Task Redelivery_books_no_second_parcel()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-DUPE", qty: 1);

        Assert.Equal(BookingOutcome.Booked, (await Book(workOrderId, serials)).Outcome);

        var replay = await Book(workOrderId, serials);

        Assert.Equal(BookingOutcome.AlreadyBooked, replay.Outcome);
        Assert.Equal(1, await ShipmentCount(workOrderId));
        // No second announcement, so 7.2's dispatch consumer can't be triggered twice either.
        Assert.Single(PublishedFor<ShipmentScheduled>(workOrderId));
    }

    [Fact]
    public async Task Simultaneous_deliveries_book_exactly_one_parcel()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-RACE", qty: 1);

        // Both pass the cheap pre-check; the unique index on shipments.work_order_id is what
        // actually decides it, and the loser's whole batch — parcel, lines and history note —
        // rolls back with its failed insert.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Book(workOrderId, serials)));

        Assert.Single(results, result => result.Outcome == BookingOutcome.Booked);
        Assert.All(results.Where(r => r.Outcome != BookingOutcome.Booked),
            result => Assert.Equal(BookingOutcome.AlreadyBooked, result.Outcome));

        Assert.Equal(1, await ShipmentCount(workOrderId));
        Assert.Equal(1, (await ShipmentFor(workOrderId))!.Lines.Count);

        // The losers left no note behind — the whole point of committing the note with the row.
        Assert.Single(await History(workOrderId), entry => (entry.Notes ?? "").Contains("Shipment booked"));
    }

    [Fact]
    public async Task An_order_that_has_not_reached_delivery_cannot_be_shipped()
    {
        var scenario = await Seed("SHIP-EARLY", qty: 1);
        await Pick(scenario);
        await Produce(scenario, attempt: 1);

        var result = await BookByHand(scenario, carrier: null);

        Assert.Equal(BookingOutcome.NotInDelivery, result.Outcome);
        Assert.Equal(0, await ShipmentCount(scenario));
    }

    // -------------------------------------------------------- 7.2 dispatch and completion

    [Fact]
    public async Task Dispatching_the_parcel_completes_the_work_order()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-DONE", qty: 2);
        await Book(workOrderId, serials);

        var result = await Dispatch(workOrderId);

        Assert.Equal(DispatchOutcome.Dispatched, result.Outcome);
        Assert.Equal(WorkOrderStatus.Completed, await Status(workOrderId));

        var shipment = (await ShipmentFor(workOrderId))!;
        Assert.Equal(ShipmentStatus.Dispatched, shipment.Status);
        Assert.NotNull(shipment.DispatchedUtc);

        // One terminal announcement carrying the parcel's details — not a separate
        // shipment-dispatched saying the same thing at the same instant.
        var completed = Assert.Single(PublishedFor<WorkOrderCompleted>(workOrderId));
        Assert.Equal(shipment.TrackingNumber, completed.TrackingNumber);
        Assert.Equal(2, completed.SerialNumbers.Count);
    }

    [Fact]
    public async Task Redelivered_dispatch_changes_nothing()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-DISPATCH-DUPE", qty: 1);
        await Book(workOrderId, serials);
        await Dispatch(workOrderId);

        var dispatchedAt = (await ShipmentFor(workOrderId))!.DispatchedUtc;

        var replay = await Dispatch(workOrderId);

        // No run table here: the shipment's own Booked → Dispatched transition is the guard.
        Assert.Equal(DispatchOutcome.AlreadyDispatched, replay.Outcome);
        Assert.Equal(dispatchedAt, (await ShipmentFor(workOrderId))!.DispatchedUtc);
        Assert.Equal(WorkOrderStatus.Completed, await Status(workOrderId));
        Assert.Single(PublishedFor<WorkOrderCompleted>(workOrderId));
    }

    [Fact]
    public async Task With_auto_booking_off_the_order_waits_for_a_visitor_and_still_completes()
    {
        _fixture.ShippingConfig.AutoBook = false;

        var (workOrderId, serials) = await OrderInDelivery("SHIP-MANUAL", qty: 1);

        // The consumer path stops: no parcel, no event, the order rests in Delivery.
        var waiting = await Book(workOrderId, serials);

        Assert.Equal(BookingOutcome.AwaitingCarrierChoice, waiting.Outcome);
        Assert.Equal(0, await ShipmentCount(workOrderId));
        Assert.Equal(WorkOrderStatus.Delivery, await Status(workOrderId));
        Assert.Empty(PublishedFor<ShipmentScheduled>(workOrderId));
        Assert.Contains(await History(workOrderId), entry => (entry.Notes ?? "").Contains("Awaiting a carrier"));

        // A visitor picks a carrier by name, and from there nothing else is manual.
        var booked = await BookByHand(workOrderId, carrier: "Meridian Aether Post");

        Assert.Equal(BookingOutcome.Booked, booked.Outcome);
        Assert.Equal("Meridian Aether Post", booked.Carrier);

        Assert.Equal(DispatchOutcome.Dispatched, (await Dispatch(workOrderId)).Outcome);
        Assert.Equal(WorkOrderStatus.Completed, await Status(workOrderId));

        // The end state is indistinguishable from the unattended path: same parcel shape, same
        // terminal event. That is the whole claim of having two ways in and one booking.
        var shipment = (await ShipmentFor(workOrderId))!;
        Assert.Equal(ShipmentStatus.Dispatched, shipment.Status);
        Assert.Single(PublishedFor<WorkOrderCompleted>(workOrderId));
    }

    [Fact]
    public async Task A_carrier_this_factory_does_not_work_with_is_refused_before_anything_happens()
    {
        var (workOrderId, _) = await OrderInDelivery("SHIP-UNKNOWN", qty: 1);

        var result = await BookByHand(workOrderId, carrier: "Definitely Not A Carrier");

        Assert.Equal(BookingOutcome.UnknownCarrier, result.Outcome);
        Assert.Equal(0, await ShipmentCount(workOrderId));
        // Not a hold: the caller made a mistake, the factory is fine.
        Assert.Equal(WorkOrderStatus.Delivery, await Status(workOrderId));
    }

    [Fact]
    public async Task Cancelling_an_order_voids_its_booked_parcel()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-CANCEL", qty: 1);
        await Book(workOrderId, serials);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<WorkOrderHandler>();
            var response = await handler.CancelWorkOrder(workOrderId,
                new WorkOrderCommandRequest { CreatedBy = "visitor", Notes = "customer changed their mind" });
            Assert.Equal(WorkOrderCommandOutcome.Success, response.Outcome);
        }

        // A cancelled order must not leave a live parcel behind it.
        Assert.Equal(ShipmentStatus.Cancelled, (await ShipmentFor(workOrderId))!.Status);
    }

    // ------------------------------------------------- 7.3 refusal, hold, and recovery

    [Fact]
    public async Task A_refused_booking_holds_the_order_with_the_reason_and_writes_no_parcel()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-REFUSED", qty: 1);
        _fixture.Carriers.RefuseEverything("Every wagon is on the northern run.");

        var result = await Book(workOrderId, serials);

        Assert.Equal(BookingOutcome.CarrierUnavailable, result.Outcome);
        Assert.Equal(WorkOrderStatus.OnHold, await Status(workOrderId));

        // Refusals leave no row: persisting them would break the unique index that makes this
        // stage idempotent and turn a clean retry into a "find the live one" query.
        Assert.Equal(0, await ShipmentCount(workOrderId));
        Assert.Empty(PublishedFor<ShipmentScheduled>(workOrderId));

        var hold = Assert.Single(await History(workOrderId),
            entry => (entry.Notes ?? "").Contains("refused the booking"));
        Assert.Contains("northern run", hold.Notes);

        // A redelivery of the same event does not double-hold. It doesn't even reach the
        // carrier: the first refusal moved the order out of Delivery, so the state guard turns
        // the duplicate away before anything can happen twice.
        var replay = await Book(workOrderId, serials);
        Assert.Equal(BookingOutcome.NotInDelivery, replay.Outcome);
        Assert.Equal(WorkOrderStatus.OnHold, await Status(workOrderId));
        Assert.Single(await History(workOrderId), entry => (entry.Notes ?? "").Contains("refused the booking"));
    }

    [Fact]
    public async Task Releasing_an_order_held_at_delivery_re_requests_the_booking_and_it_completes()
    {
        var (workOrderId, serials) = await OrderInDelivery("SHIP-RELEASE", qty: 2);
        _fixture.Carriers.RefuseEverything();
        await Book(workOrderId, serials);
        Assert.Equal(WorkOrderStatus.OnHold, await Status(workOrderId));

        // Stock arrives / a wagon frees up, and a human releases the order.
        _fixture.Carriers.AcceptEverything();
        await Release(workOrderId);

        Assert.Equal(WorkOrderStatus.Delivery, await Status(workOrderId));

        // This is the loop that has been open since 5.3: the release republishes
        // inspection-passed, which is what the existing consumer already binds.
        var passedEvents = PublishedFor<InspectionPassed>(workOrderId);
        Assert.Equal(2, passedEvents.Count); // one from inspection, one from the release
        var rerequest = passedEvents[^1];
        Assert.Equal(2, rerequest.SerialNumbers.Count);

        // The recovery is visible rather than magic.
        Assert.Contains(await History(workOrderId),
            entry => (entry.Notes ?? "").Contains("re-requesting a carrier"));

        // Feed the republished event back through the consumer path, exactly as the worker
        // would, and the order finishes on its own from there.
        Assert.Equal(BookingOutcome.Booked, (await Book(workOrderId, rerequest.SerialNumbers)).Outcome);
        Assert.Equal(DispatchOutcome.Dispatched, (await Dispatch(workOrderId)).Outcome);
        Assert.Equal(WorkOrderStatus.Completed, await Status(workOrderId));
    }

    [Fact]
    public async Task Releasing_in_any_other_circumstance_re_requests_nothing()
    {
        // (a) An order that already has a parcel. Held for some other reason, released — the
        //     shipping stage has nothing left to do, so nothing is republished.
        var (booked, serials) = await OrderInDelivery("SHIP-RELEASE-BOOKED", qty: 1);
        await Book(booked, serials);
        var beforeBooked = PublishedFor<InspectionPassed>(booked).Count;

        await Hold(booked, "paperwork");
        await Release(booked);

        Assert.Equal(beforeBooked, PublishedFor<InspectionPassed>(booked).Count);
        Assert.Equal(WorkOrderStatus.Delivery, await Status(booked));

        // (b) An order held anywhere else in the pipeline. 5.3 and 6.3 left these inert and this
        //     story deliberately does not re-arm them — the general answer is Epic 10's.
        var early = await Seed("SHIP-RELEASE-EARLY", qty: 1);
        await Pick(early);
        await Produce(early, attempt: 1);
        await Hold(early, "operator stepped away");
        await Release(early);

        Assert.Empty(PublishedFor<InspectionPassed>(early));
        Assert.Equal(WorkOrderStatus.InProcess, await Status(early));
    }

    // ------------------------------------------------------------------------ 7.4 timeline

    [Fact]
    public async Task The_timeline_narrates_a_fully_exercised_order_in_order()
    {
        var scenario = await Seed("SHIP-TIMELINE", qty: 2);
        await Pick(scenario);
        await Produce(scenario, attempt: 1);

        // A rebuild...
        _fixture.Verdicts.FailNext(1);
        await Inspect(scenario, attempt: 1);
        _fixture.Verdicts.PassEverything();
        await Produce(scenario, attempt: 2);
        await Inspect(scenario, attempt: 2);

        // ...a carrier refusal, a hold, a release...
        _fixture.Carriers.RefuseEverything();
        await BookByHand(scenario, carrier: null);
        _fixture.Carriers.AcceptEverything();
        await Release(scenario);

        // ...and a parcel that goes out.
        await BookByHand(scenario, carrier: null);
        await Dispatch(scenario);

        var timeline = await Timeline(scenario);

        Assert.NotNull(timeline);
        Assert.Equal(scenario, timeline!.WorkOrderId);

        // Strictly ordered by time — the endpoint's whole reason to exist.
        Assert.Equal(
            timeline.Entries.Select(entry => entry.At).OrderBy(at => at),
            timeline.Entries.Select(entry => entry.At));

        var byKind = timeline.Entries.GroupBy(entry => entry.Kind).ToDictionary(g => g.Key, g => g.Count());

        Assert.Single(timeline.Entries, entry => entry.Kind == TimelineKind.Pick);
        Assert.Equal(2, byKind[TimelineKind.Build]);        // the original and the rebuild
        Assert.Equal(2, byKind[TimelineKind.Inspection]);
        Assert.Equal(3, byKind[TimelineKind.Verdict]);      // 2 built + 1 rebuilt, all judged
        Assert.Equal(2, byKind[TimelineKind.Shipment]);     // booked, then dispatched
        Assert.True(byKind[TimelineKind.State] > 5);

        // The most interesting thing the timeline knows: which unit failed, and why.
        var scrapped = Assert.Single(timeline.Entries,
            entry => entry.Kind == TimelineKind.Verdict && entry.Summary.Contains("scrapped"));
        Assert.Contains("cracked mainspring", scrapped.Summary);

        // The hold, the release and its re-request are all in there, in the state entries.
        Assert.Contains(timeline.Entries, entry => entry.Summary.Contains("refused the booking"));
        Assert.Contains(timeline.Entries, entry => entry.Summary.Contains("re-requesting a carrier"));

        // The story ends where it should.
        Assert.Equal(WorkOrderStatus.Completed, await Status(scenario));
        Assert.Contains(timeline.Entries,
            entry => entry.Kind == TimelineKind.Shipment && entry.Summary.Contains("dispatched"));
    }

    [Fact]
    public async Task An_untouched_order_still_has_a_timeline()
    {
        var scenario = await Seed("SHIP-TIMELINE-NEW", qty: 1);

        var timeline = await Timeline(scenario);

        // Creation and scheduling, and nothing else — no null-shaped holes for a client to guard.
        Assert.NotNull(timeline);
        Assert.All(timeline!.Entries, entry => Assert.Equal(TimelineKind.State, entry.Kind));
        Assert.Equal(2, timeline.Entries.Count);
    }

    [Fact]
    public async Task An_unknown_work_order_has_no_timeline()
        => Assert.Null(await Timeline(Guid.NewGuid()));

    // ------------------------------------------------------------------------- helpers

    /// <summary>The consumer path: book from the serials an <c>InspectionPassed</c> named.</summary>
    private async Task<BookingResult> Book(Guid workOrderId, IReadOnlyList<Guid> serials)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShippingService>()
            .BookForPassedInspection(workOrderId, serials);
    }

    /// <summary>The API path: book from the order's own passing units, optionally naming a carrier.</summary>
    private async Task<BookingResult> BookByHand(Guid workOrderId, string? carrier)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShippingService>()
            .BookShipment(workOrderId, carrier, "visitor");
    }

    private async Task<DispatchResult> Dispatch(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShippingService>().DispatchShipment(workOrderId);
    }

    private async Task Hold(Guid workOrderId, string reason)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var response = await scope.ServiceProvider.GetRequiredService<WorkOrderHandler>()
            .HoldWorkOrder(workOrderId, new WorkOrderCommandRequest { CreatedBy = "visitor", Notes = reason });
        Assert.Equal(WorkOrderCommandOutcome.Success, response.Outcome);
    }

    private async Task Release(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var response = await scope.ServiceProvider.GetRequiredService<WorkOrderHandler>()
            .ReleaseWorkOrder(workOrderId, new WorkOrderCommandRequest { CreatedBy = "visitor" });
        Assert.Equal(WorkOrderCommandOutcome.Success, response.Outcome);
    }

    private async Task<WorkOrderTimelineDto?> Timeline(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkOrderHandler>().GetWorkOrderTimeline(workOrderId);
    }

    private async Task Pick(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<MaterialPickingService>().PickMaterials(workOrderId);
        Assert.Equal(PickOutcome.Picked, result.Outcome);
    }

    private async Task Produce(Guid workOrderId, int attempt)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ProductionService>().Produce(workOrderId, attempt);
        Assert.Equal(ProductionOutcome.Built, result.Outcome);
    }

    private async Task<InspectionResult> Inspect(Guid workOrderId, int attempt)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InspectionService>().InspectAttempt(workOrderId, attempt);
    }

    /// <summary>An order carried all the way to Delivery, and the serials that passed.</summary>
    private async Task<(Guid WorkOrderId, List<Guid> Serials)> OrderInDelivery(string tag, uint qty)
    {
        var workOrderId = await Seed(tag, qty);
        await Pick(workOrderId);
        await Produce(workOrderId, attempt: 1);

        var inspection = await Inspect(workOrderId, attempt: 1);
        Assert.Equal(InspectionOutcome.Passed, inspection.Outcome);

        var serials = (await Units(workOrderId))
            .Where(unit => unit.Status == UnitStatus.Passed)
            .Select(unit => unit.SerialNumber)
            .ToList();

        return (workOrderId, serials);
    }

    /// <summary>An isolated product, stocked components, and one scheduled work order.</summary>
    private async Task<Guid> Seed(string tag, uint qty)
    {
        await using var context = _fixture.NewContext();

        var product = new Product($"PRD-{tag}", $"{tag} Automaton");
        var chassis = new Component($"CMP-{tag}-CHASSIS", "Chassis", onHand: 100);
        product.AddBomLine(chassis, qtyPerUnit: 1);

        var workOrder = new WorkOrder("seed", product, qty);
        workOrder.AdvanceToNextStep("seed"); // Intake -> Scheduled

        context.Products.Add(product);
        context.Components.Add(chassis);
        context.WorkOrders.Add(workOrder);
        await context.SaveChangesAsync();

        return workOrder.Id;
    }

    private async Task<WorkOrderStatus> Status(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return (await context.WorkOrders.AsNoTracking().SingleAsync(order => order.Id == workOrderId)).CurrentStatus;
    }

    private async Task<Shipment?> ShipmentFor(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.Shipments
            .AsNoTracking()
            .Include(shipment => shipment.Lines)
            .FirstOrDefaultAsync(shipment => shipment.WorkOrderId == workOrderId);
    }

    private async Task<int> ShipmentCount(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.Shipments.CountAsync(shipment => shipment.WorkOrderId == workOrderId);
    }

    private async Task<List<StockKeepingUnit>> Units(Guid workOrderId)
    {
        await using var context = _fixture.NewContext();
        return await context.StockKeepingUnits
            .AsNoTracking()
            .Where(unit => EF.Property<Guid>(unit, "work_order_id") == workOrderId)
            .ToListAsync();
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

    private IReadOnlyList<T> PublishedFor<T>(Guid workOrderId) where T : Application.Messaging.IntegrationEvent
        => _fixture.Published.OfType<T>()
            .Where(@event => WorkOrderIdOf(@event) == workOrderId)
            .ToList();

    private static Guid WorkOrderIdOf(Application.Messaging.IntegrationEvent @event) => @event switch
    {
        InspectionPassed e => e.WorkOrderId,
        ShipmentScheduled e => e.WorkOrderId,
        WorkOrderCompleted e => e.WorkOrderId,
        ReworkRequired e => e.WorkOrderId,
        _ => Guid.Empty
    };
}
