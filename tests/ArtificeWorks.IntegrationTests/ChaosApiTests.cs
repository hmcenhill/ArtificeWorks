using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Api.Controllers;
using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 12.1's injection surface over the real API: arming a fault against one order, and the refusals
/// that keep the blast radius bounded. Kept to a handful of requests so they stay clear of the
/// endpoint's rate limiter, which gets its own class with its own fresh window.
/// </summary>
public class ChaosApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ChaosApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Arming_a_live_order_returns_what_was_armed_against_which_order()
    {
        var order = await CreateOrder();

        var response = await _fixture.Client.PostAsJsonAsync(
            "/system/chaos?armedBy=test", new ChaosRequest(order.Id, InjectedFaultKind.FailInspection));

        response.EnsureSuccessStatusCode();
        var armed = await response.Content.ReadFromJsonAsync<ChaosArmedDto>();

        Assert.NotNull(armed);
        Assert.Equal(order.Id, armed!.WorkOrderId);
        Assert.Equal(nameof(InjectedFaultKind.FailInspection), armed.Kind);
        Assert.Equal("test", armed.ArmedBy);
    }

    [Fact]
    public async Task Arming_an_order_that_does_not_exist_is_a_404_with_a_stable_code()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/system/chaos", new ChaosRequest(Guid.NewGuid(), InjectedFaultKind.FailInspection));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("chaos_target_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Arming_a_terminal_order_is_refused_as_not_injectable()
    {
        var order = await CreateOrder();

        // Cancelling reaches a terminal state through the ordinary command surface.
        var cancel = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{order.Id}/cancel", new WorkOrderCommandRequest { CreatedBy = "chaos-test" });
        cancel.EnsureSuccessStatusCode();

        var response = await _fixture.Client.PostAsJsonAsync(
            "/system/chaos", new ChaosRequest(order.Id, InjectedFaultKind.FailInspection));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("chaos_target_not_injectable", await response.ReadProblemCodeAsync());
    }

    private const string ProductId = "CHAOS-API-001";

    private async Task<WorkOrderDto> CreateOrder()
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "chaos-test",
            ProductId = ProductId,
            ProductName = "Chaos Automaton",
        });

        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "chaos-test",
            ItemId = ProductId,
            Qty = 1,
        });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<WorkOrderDto>())!;
    }
}

/// <summary>
/// The rate limiter in isolation (12.1), on its own fresh app so the fixed window starts empty. A
/// burst past the permit limit must be turned away — the "visitors can't grief each other" criterion.
/// </summary>
public class ChaosRateLimitTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ChaosRateLimitTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_burst_of_injections_is_rate_limited()
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "rate-test",
            ProductId = "CHAOS-RATE-001",
            ProductName = "Rate Automaton",
        });

        var created = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "rate-test",
            ItemId = "CHAOS-RATE-001",
            Qty = 1,
        });
        created.EnsureSuccessStatusCode();
        var order = (await created.Content.ReadFromJsonAsync<WorkOrderDto>())!;

        // Twelve arms at one order (arming is idempotent, so the successful ones are all 200s).
        // The fixed window permits a few, then turns the rest away with 429 + a stable code.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            var response = await _fixture.Client.PostAsJsonAsync(
                "/system/chaos", new ChaosRequest(order.Id, InjectedFaultKind.FailInspection));
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
