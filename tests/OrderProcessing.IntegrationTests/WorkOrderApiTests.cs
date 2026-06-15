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
}