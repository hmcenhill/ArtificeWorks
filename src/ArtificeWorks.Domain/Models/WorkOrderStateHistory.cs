namespace ArtificeWorks.Domain.Models;

public class WorkOrderStateHistory
{
    public Guid Id { get; }
    public Guid WorkOrderId { get; }
    public WorkOrderStatus Status { get; }
    public DateTime ChangedUtc { get; }
    public string? Notes { get; }
    public string CompletedBy { get; }

    public WorkOrder WorkOrder { get; }

    private WorkOrderStateHistory() { }

    public WorkOrderStateHistory(WorkOrder workOrder, string completedBy, string? notes)
    {
        Id = Guid.NewGuid();
        WorkOrderId = workOrder.Id;
        Status = workOrder.CurrentStatus;
        ChangedUtc = DateTime.UtcNow;
        Notes = notes;
        CompletedBy = completedBy;
        WorkOrder = workOrder;
    }
}