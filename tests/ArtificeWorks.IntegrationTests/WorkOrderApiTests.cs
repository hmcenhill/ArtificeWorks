using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Domain.Models;

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

        // Assert — reason code, not error string (the wire contract)
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("product_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task CreateWorkOrder_ZeroQuantity_ReturnsValidationProblem()
    {
        // Arrange
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Item-Qty-000",
            ProductName = "Qty Test Product"
        });

        // Act — qty 0 must be rejected at the API boundary, not as a 500 from the domain
        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = "Item-Qty-000",
            Qty = 0
        });

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadProblemCodeAsync());
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
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
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
        Assert.Equal("not_held", await response.ReadProblemCodeAsync());
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
        Assert.Equal("terminal_state", await response.ReadProblemCodeAsync());
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
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task AdvanceWorkOrder_MissingOrder_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{Guid.NewGuid()}/advance",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task GetWorkOrderHistory_MissingOrder_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/work-orders/{Guid.NewGuid()}/history");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("work_order_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task HoldWorkOrder_AlreadyHeld_ReturnsConflictWithReason()
    {
        // Arrange — hold once
        var created = await CreateWorkOrder("Item-Hold-Twice-001");
        await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/hold",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Act — holding an already-held order is rejected
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created.Id}/hold",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_held", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task AdvanceWorkOrder_WhileHeld_ReturnsConflictWithReason()
    {
        // Arrange — put the order on hold
        var created = await CreateWorkOrder("Item-Advance-Held-001");
        await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created!.Id}/hold",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Act — a held order must be released before it can advance
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created.Id}/advance",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("must_release_first", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task FullLifecycle_AdvancesThroughEveryStageToCompleted()
    {
        // Arrange
        var created = await CreateWorkOrder("Item-Lifecycle-001");

        // The manufacturing pipeline in order, each reached by one advance from Intake.
        var pipeline = new[]
        {
            WorkOrderStatus.Scheduled,
            WorkOrderStatus.InProcess,
            WorkOrderStatus.Inspection,
            WorkOrderStatus.Delivery,
            WorkOrderStatus.Completed
        };

        // Act + Assert — advance one stage at a time, asserting the new status and
        // that history grows by exactly one entry per step.
        var expectedHistoryCount = 1; // Intake, recorded at creation
        foreach (var expectedStatus in pipeline)
        {
            var response = await _fixture.Client.PostAsJsonAsync(
                $"/work-orders/{created!.Id}/advance",
                new WorkOrderCommandRequest { CreatedBy = "Line Lead", Notes = $"-> {expectedStatus}" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
            Assert.Equal(expectedStatus, dto!.Status);

            expectedHistoryCount++;
            var history = await _fixture.Client.GetFromJsonAsync<WorkOrderHistoryDto>(
                $"/work-orders/{created.Id}/history");
            Assert.Equal(expectedHistoryCount, history!.History.Count);
            Assert.Equal(expectedStatus, history.History.Last().Status);
        }

        // Assert — the persisted history is the full ordered walk, Intake first.
        var finalHistory = await _fixture.Client.GetFromJsonAsync<WorkOrderHistoryDto>(
            $"/work-orders/{created!.Id}/history");
        Assert.Equal(
            new[]
            {
                WorkOrderStatus.Intake,
                WorkOrderStatus.Scheduled,
                WorkOrderStatus.InProcess,
                WorkOrderStatus.Inspection,
                WorkOrderStatus.Delivery,
                WorkOrderStatus.Completed
            },
            finalHistory!.History.Select(h => h.Status));

        // Assert — a completed order is terminal; advancing again is rejected.
        var terminal = await _fixture.Client.PostAsJsonAsync(
            $"/work-orders/{created.Id}/advance",
            new WorkOrderCommandRequest { CreatedBy = "Line Lead" });
        Assert.Equal(HttpStatusCode.Conflict, terminal.StatusCode);
        Assert.Equal("terminal_state", await terminal.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task ConcurrentCreate_PersistsEveryOrderExactlyOnce()
    {
        // Arrange — one shared product, many simultaneous work orders against it.
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Item-Concurrent-001",
            ProductName = "Concurrency Test Product"
        });

        const int concurrency = 20;
        var request = new CreateWorkOrderRequest
        {
            Requestor = "Jane Tester",
            ItemId = "Item-Concurrent-001",
            Qty = 1
        };

        // Act — fire all creates at once, each on its own request scope/DbContext.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, concurrency)
                .Select(_ => _fixture.Client.PostAsJsonAsync("/work-orders", request)));

        // Assert — every request created a distinct order (no lost writes)...
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = new List<Guid>();
        foreach (var r in responses)
        {
            var dto = await r.Content.ReadFromJsonAsync<WorkOrderDto>();
            ids.Add(dto!.Id);
        }
        Assert.Equal(concurrency, ids.Distinct().Count());

        // ...and each persisted order has exactly one Intake history entry — no
        // cross-talk or duplicated history between the concurrent writes.
        foreach (var id in ids)
        {
            var history = await _fixture.Client.GetFromJsonAsync<WorkOrderHistoryDto>(
                $"/work-orders/{id}/history");
            var entry = Assert.Single(history!.History);
            Assert.Equal(WorkOrderStatus.Intake, entry.Status);
        }
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