using Microsoft.EntityFrameworkCore;

using OrderProcessing.Domain.Models;
using OrderProcessing.Domain.Models.Materials;

namespace OrderProcessing.IntegrationTests;

public class WorkOrderTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public WorkOrderTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WorkOrder_CreateSaveRetrieve()
    {
        // Arrange
        var defaultProduct = new Product("Item-001", "Default Product");
        var workOrder = new WorkOrder("Macho Man", defaultProduct, 3, "The cream rises to the top!");
        var sku = new StockKeepingUnit(defaultProduct);
        workOrder.AdvanceToNextStep("Elizabeth", "oh yeah!");
        workOrder.AssignSku(sku);
        workOrder.SetHold("Macho Man", "Oh no!");
        workOrder.ReleaseHold("Macho Man", "Oh yeah!");

        // Act
        _fixture.Context.WorkOrders.Add(workOrder);
        await _fixture.Context.SaveChangesAsync();

        var newContext = await _fixture.GetNewWorkOrderProcessingDbContext();
        var workOrderRetreived = newContext.WorkOrders
            .Include(w => w.AssignedStock)
            .Include(w => w.StateHistory)
            .FirstOrDefault(wo => wo.Id == workOrder.Id);

        // Assert
        Assert.Equal(workOrder.Id, workOrderRetreived?.Id);
        Assert.Equal(workOrder.CurrentStatus, workOrderRetreived?.CurrentStatus);
        Assert.Equal(workOrder.StateHistory.Count, workOrderRetreived?.StateHistory.Count);
        Assert.Equal(workOrder.StateHistory.First().Status, workOrderRetreived?.StateHistory.First().Status);
        Assert.Equal(workOrder.AssignedStock.Count, workOrderRetreived?.AssignedStock.Count);
        Assert.Equal(workOrder.AssignedStock.First().SerialNumber, workOrderRetreived?.AssignedStock.First().SerialNumber);
    }
}
