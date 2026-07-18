using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;

namespace ArtificeWorks.IntegrationTests;

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

    [Fact]
    public async Task AdvanceWorkOrder_TransitionsToNextStatusAndRecordsHistory()
    {
        // Arrange
        var created = await CreateWorkOrder("Item-Advance-001");

        // Act
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/advance",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead", Notes = "kick off scheduling" });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var advanced = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
        Assert.NotNull(advanced);
        Assert.Equal(Domain.Models.WorkOrderStatus.Scheduled, advanced.Status);

        var historyResponse = await _fixture.Client.GetFromJsonAsync<WorkOrderHistoryDto>(
            $"/work-orders/{created.Id}/history");
        Assert.NotNull(historyResponse);
        Assert.Equal(2, historyResponse.History.Count);
        Assert.Equal(Domain.Models.WorkOrderStatus.Scheduled, historyResponse.History.Last().Status);
    }

    [Fact]
    public async Task HoldThenReleaseWorkOrder_ReturnsToPreviousStatus()
    {
        // Arrange
        var created = await CreateWorkOrder("Item-Hold-001");

        // Act — hold
        var holdResponse = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/hold",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });
        var held = await holdResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Act — release
        var releaseResponse = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created.Id}/release",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });
        var released = await releaseResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, holdResponse.StatusCode);
        Assert.Equal(Domain.Models.WorkOrderStatus.OnHold, held!.Status);
        Assert.Equal(System.Net.HttpStatusCode.OK, releaseResponse.StatusCode);
        Assert.Equal(Domain.Models.WorkOrderStatus.Intake, released!.Status);
    }

    [Fact]
    public async Task ReleaseWorkOrder_NotHeld_ReturnsConflictWithReason()
    {
        // Arrange
        var created = await CreateWorkOrder("Item-Release-001");

        // Act — releasing an order that was never held
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/release",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        var reason = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task CancelWorkOrder_TransitionsToCancelledAndRecordsHistory()
    {
        // Arrange
        var created = await CreateWorkOrder("Item-Cancel-001");

        // Act
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/cancel",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead", Notes = "customer withdrew order" });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var cancelled = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
        Assert.NotNull(cancelled);
        Assert.Equal(Domain.Models.WorkOrderStatus.Cancelled, cancelled.Status);

        var historyResponse = await _fixture.Client.GetFromJsonAsync<WorkOrderHistoryDto>(
            $"/work-orders/{created.Id}/history");
        Assert.NotNull(historyResponse);
        Assert.Equal(Domain.Models.WorkOrderStatus.Cancelled, historyResponse.History.Last().Status);
    }

    [Fact]
    public async Task CancelWorkOrder_AlreadyCompleted_ReturnsConflictWithReason()
    {
        // Arrange — drive the order all the way to Completed (Intake -> ... -> Completed)
        var created = await CreateWorkOrder("Item-Cancel-Completed-001");
        for (var i = 0; i < 5; i++)
        {
            await _fixture.Client.PostAsJsonAsync(
                $"/work-orders/{created!.Id}/advance",
                new WorkOrderCommandRequest { CreatedBy = "Line Lead" });
        }

        // Act — cancelling a terminal order must be rejected
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/cancel",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        var reason = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task CancelWorkOrder_MissingOrder_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{Guid.NewGuid()}/cancel",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdvanceWorkOrder_MissingOrder_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{Guid.NewGuid()}/advance",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<WorkOrderDto?> CreateWorkOrder(string productId)
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = productId,
            ProductName = "Lifecycle Test Product"
        });
        var createResponse = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = productId,
            Qty = 3
        });
        return await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();
    }
}