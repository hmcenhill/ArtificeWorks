using System.Net.Http.Json;

using OrderProcessing.Application.Commands;
using OrderProcessing.Application.Data;

namespace OrderProcessing.IntegrationTests;

public class WorkOrderApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public WorkOrderApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateWorkOrder_CreatesAndReturnsCreatedWorkOrderSuccessfully()
    {
        // Arrange
        var productRequest = new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Item-001",
            ProductName = "Default Product"
        };
        var workOrderRequest = new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = "Item-001",
            Qty = 5,
            Notes = "test order"
        };
        var productResponse = await _fixture.Client.PostAsJsonAsync("/products", productRequest);
        Assert.True(productResponse.IsSuccessStatusCode);
        // Act
        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", workOrderRequest);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var workOrderResponse = await response.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.NotNull(workOrderResponse);
        Assert.Equal(Domain.Models.WorkOrderStatus.Intake, workOrderResponse.Status);
    }

    [Fact]
    public async Task CreateWorkOrder_InvalidProductDoesNotCreateWorkOrder()
    {
        // Arrange
        var productRequest = new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Item-002",
            ProductName = "Default Product"
        };
        var nonExistantItemId = "Doesnt-Exist-Item";
        var workOrderRequest = new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = nonExistantItemId,
            Qty = 5,
            Notes = "test order"
        };
        var productResponse = await _fixture.Client.PostAsJsonAsync("/products", productRequest);
        Assert.True(productResponse.IsSuccessStatusCode);
        // Act
        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", workOrderRequest);

        // Assert
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        var errorMessage = await response.Content.ReadAsStringAsync();
        var expectedErrorMessage = $"Error creating work order: no product found with id: {nonExistantItemId}";

        Assert.Equal(expectedErrorMessage, errorMessage);
    }

    [Fact]
    public async Task GetWorkOrder_ReturnsExistingWorkOrder()
    {
        // Arrange — create a product and work order first
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Item-Get-001",
            ProductName = "Get Test Product"
        });
        var createResponse = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = "Item-Get-001",
            Qty = 3
        });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Act
        var response = await _fixture.Client.GetAsync($"/work-orders/{created!.Id}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var workOrder = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
        Assert.NotNull(workOrder);
        Assert.Equal(created.Id, workOrder.Id);
        Assert.Equal(Domain.Models.WorkOrderStatus.Intake, workOrder.Status);
        Assert.Equal("Item-Get-001", workOrder.OrderedItemId);
    }

    [Fact]
    public async Task GetWorkOrder_ReturnsNotFoundForMissingOrder()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/work-orders/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetWorkOrderHistory_ReturnsHistoryForExistingOrder()
    {
        // Arrange
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Item-History-001",
            ProductName = "History Test Product"
        });
        var createResponse = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = "Item-History-001",
            Qty = 2,
            Notes = "history test"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Act
        var response = await _fixture.Client.GetAsync($"/work-orders/{created!.Id}/history");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<WorkOrderHistoryDto>();
        Assert.NotNull(history);
        Assert.Equal(created.Id, history.WorkOrderId);
        Assert.Single(history.History);
        Assert.Equal(Domain.Models.WorkOrderStatus.Intake, history.History.First().Status);
    }
}