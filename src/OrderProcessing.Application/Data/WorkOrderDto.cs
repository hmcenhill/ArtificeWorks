using OrderProcessing.Domain.Models;

namespace OrderProcessing.Application.Data;

public class WorkOrderDto
{
    public Guid Id { get; set; }
    public WorkOrderStatus Status { get; set; }
    public string OrderedItemId { get; set; } = string.Empty;
    public uint OrderItemQty { get; set; }

    public WorkOrderDto() { }
    public WorkOrderDto(WorkOrder workOrder)
    {
        Id = workOrder.Id;
        Status = workOrder.CurrentStatus;
        OrderedItemId = workOrder.OrderedItem.ItemId;
        OrderItemQty = workOrder.OrderItemQty;
    }
}

public class WorkOrderHistoryDto
{
    public Guid WorkOrderId { get; set; }
    public List<StateHistoryEntryDto> History { get; set; } = [];

    public WorkOrderHistoryDto() { }
    public WorkOrderHistoryDto(WorkOrder workOrder)
    {
        WorkOrderId = workOrder.Id;
        History = workOrder.StateHistory
            .OrderBy(h => h.ChangedUtc)
            .Select(h => new StateHistoryEntryDto(h))
            .ToList();
    }
}

public class StateHistoryEntryDto
{
    public WorkOrderStatus Status { get; set; }
    public DateTime ChangedUtc { get; set; }
    public string? Notes { get; set; }
    public string CompletedBy { get; set; } = string.Empty;

    public StateHistoryEntryDto() { }
    public StateHistoryEntryDto(WorkOrderStateHistory entry)
    {
        Status = entry.Status;
        ChangedUtc = entry.ChangedUtc;
        Notes = entry.Notes;
        CompletedBy = entry.CompletedBy;
    }
}