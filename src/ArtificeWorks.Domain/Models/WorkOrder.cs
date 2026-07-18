using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Domain.Models;

public class WorkOrder
{
    public Guid Id { get; }
    public WorkOrderStatus CurrentStatus { get; private set; }
    public WorkOrderStatus? PreviousStatus { get; private set; }
    public DateTime CreatedUtc { get; }
    public DateTime UpdatedUtc { get; private set; }

    public Product OrderedItem { get; }
    public uint OrderItemQty { get; }
    public IReadOnlyList<StockKeepingUnit> AssignedStock { get => _assignedStock.AsReadOnly(); }
    private readonly IList<StockKeepingUnit> _assignedStock;

    public IList<WorkOrderStateHistory> StateHistory { get; private set; }

    private readonly ICollection<WorkOrderStatus> _cantAdvanceStatuses = new HashSet<WorkOrderStatus>
        {
            WorkOrderStatus.OnHold,
            WorkOrderStatus.Fault
        };

    private readonly ICollection<WorkOrderStatus> _holdStatuses = new HashSet<WorkOrderStatus>
        {
            WorkOrderStatus.OnHold,
            WorkOrderStatus.Fault
        };

    private WorkOrder() { }

    public WorkOrder(string createdBy, Product item, uint qty, string? notes = null)
    {
        if (qty <= 0)
        {
            throw new ArgumentOutOfRangeException("Order Qty must be greater than 0");
        }

        Id = Guid.NewGuid();
        CurrentStatus = WorkOrderStatus.Intake;
        CreatedUtc = DateTime.UtcNow;
        UpdatedUtc = CreatedUtc;
        OrderedItem = item;
        OrderItemQty = qty;
        _assignedStock = new List<StockKeepingUnit>();
        StateHistory = new List<WorkOrderStateHistory>
        {
            new WorkOrderStateHistory(this, createdBy, notes)
        };
    }

    public void SetStatus(WorkOrderStatus newStatus, string createdBy, string? notes = null) // Superuser command
    {
        PreviousStatus = CurrentStatus;
        CurrentStatus = newStatus;
        UpdateStateHistory(createdBy, notes);
    }

    public TransitionResult AdvanceToNextStep(string createdBy, string? notes = null)
    {
        if (CurrentStatus == WorkOrderStatus.Completed)
        {
            return TransitionResult.Rejected("Work order is already Completed and cannot be advanced further.");
        }
        if (!CanAdvance())
        {
            return TransitionResult.Rejected($"Work order cannot be advanced while it is {CurrentStatus}. Release it first.");
        }
        PreviousStatus = CurrentStatus;
        CurrentStatus = GetNextStatus(CurrentStatus);
        UpdateStateHistory(createdBy, notes);

        return TransitionResult.Ok();
    }

    private bool CanAdvance() => !_cantAdvanceStatuses.Contains(CurrentStatus);

    public TransitionResult SetHold(string createdBy, string? notes = null)
    {
        if (_holdStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected($"Work order is already {CurrentStatus} and cannot be held again.");
        }
        PreviousStatus = CurrentStatus;
        CurrentStatus = WorkOrderStatus.OnHold;
        UpdateStateHistory(createdBy, notes);
        return TransitionResult.Ok();
    }

    public TransitionResult ReleaseHold(string createdBy, string? notes = null)
    {
        if (!_holdStatuses.Contains(CurrentStatus) || PreviousStatus is null)
        {
            return TransitionResult.Rejected($"Work order is {CurrentStatus} and is not currently held; nothing to release.");
        }
        CurrentStatus = PreviousStatus.Value;
        PreviousStatus = null;
        UpdateStateHistory(createdBy, notes);
        return TransitionResult.Ok();
    }

    private void UpdateStateHistory(string createdBy, string? notes = null)
    {
        UpdatedUtc = DateTime.UtcNow;
        StateHistory.Add(new WorkOrderStateHistory(this, createdBy, notes));
    }

    private static WorkOrderStatus GetNextStatus(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Intake => WorkOrderStatus.Scheduled,
        WorkOrderStatus.Scheduled => WorkOrderStatus.InProcess,
        WorkOrderStatus.InProcess => WorkOrderStatus.Inspection,
        WorkOrderStatus.Inspection => WorkOrderStatus.Delivery,
        WorkOrderStatus.Delivery => WorkOrderStatus.Completed,
        _ => WorkOrderStatus.Fault,
    };

    public bool AssignSku(StockKeepingUnit unit)
    {
        if (!OrderedItem.ItemId.Equals(unit.Product.ItemId) || _assignedStock.Count >= OrderItemQty)
        {
            // Wrong Product or qty already fulfilled
            return false;
        }

        _assignedStock.Add(unit);
        if (_assignedStock.Count >= OrderItemQty)
        {
            // TODO: Order fulfilled! whoa!
        }
        return true;
    }

    public bool UnassignSku(Guid serialNumber)
    {
        var unit = _assignedStock.FirstOrDefault(sku => sku.SerialNumber == serialNumber);
        if (unit is not null)
        {
            _assignedStock.Remove(unit);
            return true;
        }
        return false;
    }
}
