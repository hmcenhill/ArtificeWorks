using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The manual verdict endpoint (6.2) over HTTP: the visitor's decision moment, and the path
/// Epic 12's failure injection reuses instead of inventing a back door. What this class adds
/// over the service-level tests is the wire contract — the status codes and the stable
/// ProblemDetails <c>code</c> for every way a verdict can be refused.
/// </summary>
public class InspectionApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public InspectionApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Passing_every_unit_by_hand_carries_the_order_to_delivery()
    {
        var workOrder = await OrderInInspection("INSP-API-OK", qty: 2);
        var serials = workOrder.Units.Select(unit => unit.SerialNumber).ToList();

        var first = await Verdict(workOrder.Id, serials[0], passed: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var afterFirst = await first.Content.ReadFromJsonAsync<WorkOrderDto>();
        Assert.Equal(WorkOrderStatus.Inspection, afterFirst!.Status);
        Assert.Equal(1u, afterFirst.PassedQty);

        // Verdicts are visible on the read side — a failure never has to be dug out of a log.
        Assert.Equal(UnitStatus.Passed, afterFirst.Units.Single(u => u.SerialNumber == serials[0]).Status);
        Assert.Equal(UnitStatus.Built, afterFirst.Units.Single(u => u.SerialNumber == serials[1]).Status);

        var second = await Verdict(workOrder.Id, serials[1], passed: true);
        var afterSecond = await second.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(WorkOrderStatus.Delivery, afterSecond!.Status);
        Assert.Equal(2u, afterSecond.PassedQty);
    }

    [Fact]
    public async Task Failing_a_unit_records_its_reason_and_returns_the_order_to_production()
    {
        var workOrder = await OrderInInspection("INSP-API-FAIL", qty: 1);
        var serial = workOrder.Units[0].SerialNumber;

        var response = await Verdict(workOrder.Id, serial, passed: false, reason: "cracked mainspring");
        var updated = await response.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WorkOrderStatus.InProcess, updated!.Status);

        var unit = updated.Units.Single();
        Assert.Equal(UnitStatus.Scrapped, unit.Status);
        Assert.Equal("cracked mainspring", unit.ScrapReason);
        Assert.NotNull(unit.InspectedUtc);
    }

    [Fact]
    public async Task A_unit_cannot_be_verdicted_twice()
    {
        var workOrder = await OrderInInspection("INSP-API-DUPE", qty: 2);
        var serial = workOrder.Units[0].SerialNumber;

        await Verdict(workOrder.Id, serial, passed: true);
        var again = await Verdict(workOrder.Id, serial, passed: false, reason: "second thoughts");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("unit_already_inspected", await again.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task An_unknown_serial_number_is_not_found()
    {
        var workOrder = await OrderInInspection("INSP-API-UNKNOWN", qty: 1);

        var response = await Verdict(workOrder.Id, Guid.NewGuid(), passed: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unit_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task A_unit_is_not_up_for_judgement_until_the_order_reaches_inspection()
    {
        var workOrder = await OrderInProcess("INSP-API-EARLY", qty: 1);

        var response = await Verdict(workOrder.Id, workOrder.Units[0].SerialNumber, passed: true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("order_not_in_inspection", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task A_failing_verdict_must_carry_a_reason()
    {
        var workOrder = await OrderInInspection("INSP-API-NOREASON", qty: 1);

        var response = await Verdict(workOrder.Id, workOrder.Units[0].SerialNumber, passed: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("scrap_reason_required", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task A_verdict_against_an_unknown_work_order_is_not_found()
    {
        var response = await Verdict(Guid.NewGuid(), Guid.NewGuid(), passed: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
    }

    // ------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> Verdict(Guid workOrderId, Guid serialNumber, bool passed, string? reason = null)
        => _fixture.Client.PostAsJsonAsync($"/work-orders/{workOrderId}/inspections", new RecordVerdictRequest
        {
            SerialNumber = serialNumber,
            Passed = passed,
            Reason = reason,
            CreatedBy = "visitor"
        });

    /// <summary>
    /// A work order with its units built. Production is only ever triggered by an event, so
    /// this reaches into the API's own container and runs the workflow directly — the same
    /// service the worker calls.
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
        var production = scope.ServiceProvider.GetRequiredService<ProductionService>();
        var result = await production.Produce(created.Id, attemptNumber: 1);
        Assert.Equal(ProductionOutcome.Built, result.Outcome);

        return await Read(created.Id);
    }

    /// <summary>The same, advanced into Inspection so its units are up for judgement.</summary>
    private async Task<WorkOrderDto> OrderInInspection(string productId, uint qty)
    {
        var workOrder = await OrderInProcess(productId, qty);
        await Advance(workOrder.Id); // InProcess -> Inspection
        return await Read(workOrder.Id);
    }

    private Task<HttpResponseMessage> Advance(Guid workOrderId)
        => _fixture.Client.PostAsJsonAsync($"/work-orders/{workOrderId}/advance",
            new WorkOrderCommandRequest { CreatedBy = "seed" });

    private async Task<WorkOrderDto> Read(Guid workOrderId)
        => (await _fixture.Client.GetFromJsonAsync<WorkOrderDto>($"/work-orders/{workOrderId}"))!;
}
