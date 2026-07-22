using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Shipping;

using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The manual booking endpoint (7.2/7.3) and the timeline endpoint (7.4) over HTTP. What this
/// adds over <see cref="ShippingWorkflowTests"/> is the wire contract: status codes, the stable
/// ProblemDetails <c>code</c> for every way a booking can be refused, and the shipment's shape on
/// the work order read model.
/// </summary>
public class ShippingApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ShippingApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
        _fixture.Carriers.AcceptEverything();
    }

    [Fact]
    public async Task Booking_by_hand_puts_the_parcel_on_the_work_order()
    {
        var order = await OrderInDelivery("SHIP-API-OK", qty: 2);

        var response = await Book(order.Id, carrier: "Ravenscroft Haulage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
        Assert.NotNull(updated!.Shipment);
        Assert.Equal("Ravenscroft Haulage", updated.Shipment!.Carrier);
        Assert.Equal(ShipmentStatus.Booked, updated.Shipment.Status);
        Assert.False(string.IsNullOrWhiteSpace(updated.Shipment.TrackingNumber));
        Assert.Equal(2, updated.Shipment.SerialNumbers.Count);
        Assert.Null(updated.Shipment.DispatchedUtc);

        // Booking is not a manufacturing transition: the order was already in Delivery.
        Assert.Equal(WorkOrderStatus.Delivery, updated.Status);
    }

    [Fact]
    public async Task An_order_may_only_be_booked_once()
    {
        var order = await OrderInDelivery("SHIP-API-DUPE", qty: 1);
        await Book(order.Id, carrier: null);

        var again = await Book(order.Id, carrier: null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("shipment_already_booked", await again.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task An_order_that_has_not_reached_delivery_cannot_be_booked()
    {
        var order = await OrderInProcess("SHIP-API-EARLY", qty: 1);

        var response = await Book(order.Id, carrier: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("order_not_in_delivery", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task A_carrier_this_factory_does_not_work_with_is_a_bad_request()
    {
        var order = await OrderInDelivery("SHIP-API-UNKNOWN", qty: 1);

        var response = await Book(order.Id, carrier: "Definitely Not A Carrier");

        // 400, not 409: the caller named something that doesn't exist. Distinct from a carrier
        // that exists and won't take the job, which is the world's problem, not the caller's.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown_carrier", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task A_carrier_with_no_capacity_is_a_conflict_and_holds_the_order()
    {
        var order = await OrderInDelivery("SHIP-API-REFUSED", qty: 1);
        _fixture.Carriers.RefuseEverything("Every wagon is on the northern run.");

        var response = await Book(order.Id, carrier: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("carrier_unavailable", await response.ReadProblemCodeAsync());

        var held = await Read(order.Id);
        Assert.Equal(WorkOrderStatus.OnHold, held.Status);
        Assert.Null(held.Shipment); // a refusal leaves no row at all
    }

    [Fact]
    public async Task Booking_against_an_unknown_work_order_is_not_found()
    {
        var response = await Book(Guid.NewGuid(), carrier: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
    }

    // ------------------------------------------------------------------------ 7.4 timeline

    [Fact]
    public async Task The_timeline_endpoint_returns_the_order_s_whole_story()
    {
        var order = await OrderInDelivery("SHIP-API-TIMELINE", qty: 1);
        await Book(order.Id, carrier: null);

        var timeline = await _fixture.Client.GetFromJsonAsync<WorkOrderTimelineDto>(
            $"/work-orders/{order.Id}/timeline");

        Assert.NotNull(timeline);
        Assert.Equal(order.Id, timeline!.WorkOrderId);

        // Typed kinds a dashboard can switch on, in one flat chronological array.
        Assert.Contains(timeline.Entries, entry => entry.Kind == TimelineKind.State);
        Assert.Contains(timeline.Entries, entry => entry.Kind == TimelineKind.Build);
        Assert.Contains(timeline.Entries, entry => entry.Kind == TimelineKind.Inspection);
        Assert.Contains(timeline.Entries, entry => entry.Kind == TimelineKind.Verdict);
        Assert.Contains(timeline.Entries, entry => entry.Kind == TimelineKind.Shipment);

        Assert.Equal(
            timeline.Entries.Select(entry => entry.At).OrderBy(at => at),
            timeline.Entries.Select(entry => entry.At));

        // Every entry carries its own one-line summary, so a client can render the list before
        // it understands a single detail payload.
        Assert.All(timeline.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Summary)));
    }

    [Fact]
    public async Task An_unknown_work_order_has_no_timeline()
    {
        var response = await _fixture.Client.GetAsync($"/work-orders/{Guid.NewGuid()}/timeline");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
    }

    // ------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> Book(Guid workOrderId, string? carrier)
        => _fixture.Client.PostAsJsonAsync($"/work-orders/{workOrderId}/shipments", new BookShipmentRequest
        {
            Carrier = carrier,
            CreatedBy = "visitor"
        });

    /// <summary>
    /// A work order with its units built. Production is only ever triggered by an event, so this
    /// reaches into the API's own container and runs the workflow directly — the same service
    /// the worker calls.
    /// </summary>
    private async Task<WorkOrderDto> OrderInProcess(string productId, uint qty)
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "seed",
            ProductId = productId,
            ProductName = $"{productId} Automaton"
        });

        var created = await (await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "seed",
            ItemId = productId,
            Qty = qty
        })).Content.ReadFromJsonAsync<WorkOrderDto>();

        await Advance(created!.Id); // Intake -> Scheduled

        await using var scope = _fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ProductionService>()
            .Produce(created.Id, attemptNumber: 1);
        Assert.Equal(ProductionOutcome.Built, result.Outcome);

        return await Read(created.Id);
    }

    /// <summary>The same, inspected and passed, so it is resting in Delivery awaiting a carrier.</summary>
    private async Task<WorkOrderDto> OrderInDelivery(string productId, uint qty)
    {
        var order = await OrderInProcess(productId, qty);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<InspectionService>()
                .InspectAttempt(order.Id, attemptNumber: 1);
            Assert.Equal(InspectionOutcome.Passed, result.Outcome);
        }

        var delivered = await Read(order.Id);
        Assert.Equal(WorkOrderStatus.Delivery, delivered.Status);
        return delivered;
    }

    private Task<HttpResponseMessage> Advance(Guid workOrderId)
        => _fixture.Client.PostAsJsonAsync($"/work-orders/{workOrderId}/advance",
            new WorkOrderCommandRequest { CreatedBy = "seed" });

    private async Task<WorkOrderDto> Read(Guid workOrderId)
        => (await _fixture.Client.GetFromJsonAsync<WorkOrderDto>($"/work-orders/{workOrderId}"))!;
}
