using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The board's list read model (11.1): <c>GET /work-orders</c>, filterable by status and origin,
/// bounded and newest-first. Seeds its own orders and asserts against just those ids, so it is
/// robust to whatever else this shared fixture's database already holds.
/// </summary>
public class WorkOrderListApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public WorkOrderListApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task List_ReturnsSeededOrders_NewestFirst()
    {
        // Created oldest → newest; the board wants newest first.
        var first = await CreateOrder("List-NF-A");
        var second = await CreateOrder("List-NF-B");
        var third = await CreateOrder("List-NF-C");

        var list = await GetList();

        // Restrict to the three we just made and assert their relative order is reversed.
        var mine = list
            .Where(i => i.Id == first || i.Id == second || i.Id == third)
            .Select(i => i.Id)
            .ToList();
        Assert.Equal(new[] { third, second, first }, mine);
    }

    [Fact]
    public async Task List_FiltersByOrigin()
    {
        var visitor = await CreateOrder("List-Origin-V", WorkOrderOrigin.Visitor);
        var simulated = await CreateOrder("List-Origin-S", WorkOrderOrigin.Simulated);

        var onlySimulated = await GetList("?origin=Simulated");

        Assert.Contains(onlySimulated, i => i.Id == simulated);
        Assert.DoesNotContain(onlySimulated, i => i.Id == visitor);
        Assert.All(onlySimulated, i => Assert.Equal(WorkOrderOrigin.Simulated, i.Origin));
    }

    [Fact]
    public async Task List_FiltersByStatus_AndCombinesWithOrigin()
    {
        var visitor = await CreateOrder("List-Status-V", WorkOrderOrigin.Visitor);
        var simulated = await CreateOrder("List-Status-S", WorkOrderOrigin.Simulated);

        // Move the visitor order off Intake so a status filter can tell them apart.
        await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{visitor}/advance",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        var scheduled = await GetList("?status=Scheduled");
        Assert.Contains(scheduled, i => i.Id == visitor);
        Assert.DoesNotContain(scheduled, i => i.Id == simulated); // still Intake
        Assert.All(scheduled, i => Assert.Equal(WorkOrderStatus.Scheduled, i.Status));

        // Both filters together narrow to their intersection — empty of either of ours: the
        // visitor is Scheduled but not Simulated, the simulated one is Simulated but still Intake.
        var scheduledSimulated = await GetList("?status=Scheduled&origin=Simulated");
        Assert.DoesNotContain(scheduledSimulated, i => i.Id == visitor);
        Assert.DoesNotContain(scheduledSimulated, i => i.Id == simulated);
        Assert.All(scheduledSimulated, i =>
        {
            Assert.Equal(WorkOrderStatus.Scheduled, i.Status);
            Assert.Equal(WorkOrderOrigin.Simulated, i.Origin);
        });
    }

    [Fact]
    public async Task List_LimitBoundsTheWindow()
    {
        // Enough live orders that a small limit has to bite.
        for (var i = 0; i < 3; i++)
        {
            await CreateOrder($"List-Limit-{i}");
        }

        var limited = await GetList("?limit=2");

        Assert.Equal(2, limited.Count);
    }

    private async Task<List<WorkOrderListItemDto>> GetList(string query = "")
        => (await _fixture.Client.GetFromJsonAsync<List<WorkOrderListItemDto>>($"/work-orders{query}"))!;

    private async Task<Guid> CreateOrder(string productId, WorkOrderOrigin origin = WorkOrderOrigin.Visitor)
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = productId,
            ProductName = $"{productId} Product"
        });
        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = productId,
            Qty = 1,
            Origin = origin
        });
        var created = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
        return created!.Id;
    }
}

/// <summary>
/// The empty-factory case gets its own fixture so the database is genuinely empty: an empty
/// factory must answer <c>200</c> with <c>[]</c>, never a <c>404</c>.
/// </summary>
public class WorkOrderListEmptyApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public WorkOrderListEmptyApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task List_EmptyFactory_ReturnsEmptyArrayNotNotFound()
    {
        var response = await _fixture.Client.GetAsync("/work-orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<WorkOrderListItemDto>>();
        Assert.NotNull(list);
        Assert.Empty(list);
    }
}
