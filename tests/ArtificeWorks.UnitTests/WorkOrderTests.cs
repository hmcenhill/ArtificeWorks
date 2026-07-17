using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.UnitTests;

public class WorkOrderTests
{
    private const string DefaultUserName = "x-unit";

    [Fact]
    public void WorkOrder_InvalidWorkOrderDefinition_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 0));
    }

    [Theory]
    [InlineData(WorkOrderStatus.Intake, WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.Scheduled, WorkOrderStatus.InProcess)]
    [InlineData(WorkOrderStatus.InProcess, WorkOrderStatus.Inspection)]
    [InlineData(WorkOrderStatus.Inspection, WorkOrderStatus.Delivery)]
    [InlineData(WorkOrderStatus.Delivery, WorkOrderStatus.Completed)]
    public void WorkOrders_AdvanceCorrectlyToNextStep(WorkOrderStatus startingStatus, WorkOrderStatus expectedStatus)
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        workOrder.SetStatus(startingStatus, DefaultUserName);

        // Act
        var response = workOrder.AdvanceToNextStep(DefaultUserName);

        // Assert
        Assert.True(response);
        Assert.Equal<WorkOrderStatus>(expectedStatus, workOrder.CurrentStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Intake, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.Scheduled, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.InProcess, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.Inspection, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.Delivery, WorkOrderStatus.OnHold)]
    public void WorkOrders_CanHold(WorkOrderStatus startingStatus, WorkOrderStatus expectedStatus)
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        workOrder.SetStatus(startingStatus, DefaultUserName);

        // Act
        var response = workOrder.SetHold(DefaultUserName);

        // Assert
        Assert.True(response);
        Assert.Equal<WorkOrderStatus>(expectedStatus, workOrder.CurrentStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Intake)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProcess)]
    [InlineData(WorkOrderStatus.Inspection)]
    [InlineData(WorkOrderStatus.Delivery)]
    public void WorkOrders_HeldOrderUnholdToPreviousStatus(WorkOrderStatus startingStatus)
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        workOrder.SetStatus(startingStatus, DefaultUserName);

        // Act
        var holdResponse = workOrder.SetHold(DefaultUserName);
        var releaseHoldResponse = workOrder.ReleaseHold(DefaultUserName);

        // Assert
        Assert.True(holdResponse && releaseHoldResponse);
        Assert.Equal<WorkOrderStatus>(startingStatus, workOrder.CurrentStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Intake)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProcess)]
    [InlineData(WorkOrderStatus.Inspection)]
    [InlineData(WorkOrderStatus.Delivery)]
    public void WorkOrders_CanOnlyReleaseHeldOrders(WorkOrderStatus startingStatus)
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        workOrder.SetStatus(startingStatus, DefaultUserName);

        // Act
        var releaseHoldResponse = workOrder.ReleaseHold(DefaultUserName);

        // Assert
        Assert.False(releaseHoldResponse);
        Assert.Equal<WorkOrderStatus>(startingStatus, workOrder.CurrentStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.OnHold, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.Fault, WorkOrderStatus.Fault)]
    public void WorkOrders_HeldOrdersWontAdvance(WorkOrderStatus startingStatus, WorkOrderStatus expectedStatus)
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        workOrder.SetStatus(startingStatus, DefaultUserName);

        // Act
        var response = workOrder.AdvanceToNextStep(DefaultUserName);

        // Assert
        Assert.False(response);
        Assert.Equal<WorkOrderStatus>(expectedStatus, workOrder.CurrentStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.OnHold, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.Fault, WorkOrderStatus.Fault)]
    public void WorkOrders_CantHoldHeldOrders(WorkOrderStatus startingStatus, WorkOrderStatus expectedStatus)
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        workOrder.SetStatus(startingStatus, DefaultUserName);

        // Act
        var response = workOrder.SetHold(DefaultUserName);

        // Assert
        Assert.False(response);
        Assert.Equal<WorkOrderStatus>(expectedStatus, workOrder.CurrentStatus);
    }

    [Fact]
    public void WorkOrders_CanAssignCorrectProduct()
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        var sku = new StockKeepingUnit(TestData.DefaultProduct());

        // Act
        var response = workOrder.AssignSku(sku);

        // Assert
        Assert.True(response);
    }

    [Fact]
    public void WorkOrders_CanNotAssignWrongProduct()
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        var sku = new StockKeepingUnit(TestData.SomeOtherProduct());

        // Act
        var response = workOrder.AssignSku(sku);

        // Assert
        Assert.False(response);
        Assert.NotEqual(workOrder.OrderedItem.ItemId, sku.Product.ItemId);
    }

    [Fact]
    public void WorkOrders_CanNotAssignMoreProductThanRequested()
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        var sku1 = new StockKeepingUnit(TestData.DefaultProduct());
        var sku2 = new StockKeepingUnit(TestData.DefaultProduct());
        var sku3 = new StockKeepingUnit(TestData.DefaultProduct());
        var sku4 = new StockKeepingUnit(TestData.DefaultProduct());
        var sku5 = new StockKeepingUnit(TestData.DefaultProduct());
        var sku6 = new StockKeepingUnit(TestData.DefaultProduct());

        // Act
        var response1 = workOrder.AssignSku(sku1);
        var response2 = workOrder.AssignSku(sku2);
        var response3 = workOrder.AssignSku(sku3);
        var response4 = workOrder.AssignSku(sku4);
        var response5 = workOrder.AssignSku(sku5);
        var response6 = workOrder.AssignSku(sku6);

        // Assert
        Assert.True(response1);
        Assert.True(response2);
        Assert.True(response3);
        Assert.True(response4);
        Assert.True(response5);
        Assert.False(response6);
    }

    [Fact]
    public void WorkOrders_CanUnassignBySerialNumber()
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        var sku = new StockKeepingUnit(TestData.DefaultProduct());

        // Act
        var assignmentResponse = workOrder.AssignSku(sku);
        var unassignmentResponse = workOrder.UnassignSku(sku.SerialNumber);

        // Assert
        Assert.True(assignmentResponse);
        Assert.True(unassignmentResponse);
        Assert.Empty(workOrder.AssignedStock);
    }

    [Fact]
    public void WorkOrders_CanNotUnassignMaterialNotAlreadyAssigned()
    {
        // Arrange
        var workOrder = new WorkOrder(DefaultUserName, TestData.DefaultProduct(), 5);
        var sku = new StockKeepingUnit(TestData.DefaultProduct());
        var differentSku = new StockKeepingUnit(TestData.DefaultProduct());

        // Act
        var assignmentResponse = workOrder.AssignSku(sku);
        var unassignmentResponse = workOrder.UnassignSku(differentSku.SerialNumber);

        // Assert
        Assert.True(assignmentResponse);
        Assert.False(unassignmentResponse);
        Assert.Contains(sku, workOrder.AssignedStock);
        Assert.DoesNotContain(differentSku, workOrder.AssignedStock);
    }
}
