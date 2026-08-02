using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Shipping;

namespace ArtificeWorks.Application.Data;

public class WorkOrderDto
{
    public Guid Id { get; set; }
    public WorkOrderStatus Status { get; set; }
    public string OrderedItemId { get; set; } = string.Empty;
    public uint OrderItemQty { get; set; }

    /// <summary>Who asked for it (10.3) — so a dashboard can tell a visitor's order from a robot's.</summary>
    public WorkOrderOrigin Origin { get; set; }

    /// <summary>Units that have passed inspection, against <see cref="OrderItemQty"/>.</summary>
    public uint PassedQty { get; set; }

    /// <summary>How many production attempts the order has had; 0 before anything is built.</summary>
    public int BuildAttempt { get; set; }

    /// <summary>
    /// The serialized units built for this order and their verdicts, so a failure is visible on
    /// the API without reading the log. Empty until production runs.
    /// </summary>
    public List<StockUnitDto> Units { get; set; } = [];

    /// <summary>
    /// The parcel, once a carrier has been booked. Null until then — including for an order
    /// resting in Delivery with auto-booking off, or one held after a carrier refused, since a
    /// refusal deliberately leaves no row (7.3).
    /// </summary>
    public ShipmentDto? Shipment { get; set; }

    /// <summary>
    /// The order this one is building a sub-assembly for (13.3), or null for a top-level order.
    /// Paired with <see cref="Children"/>, this is what lets the detail view link the two
    /// directions — a child says what it is made for, a parent says what it is waiting on.
    /// </summary>
    public Guid? ParentWorkOrderId { get; set; }

    /// <summary>The component a child order's finished units are credited to. Null for a top-level order.</summary>
    public string? ForComponentId { get; set; }

    /// <summary>
    /// The sub-assembly orders spawned for this one, newest last. Empty for the overwhelming
    /// majority of orders, which is why the board's list DTO carries only a flag.
    /// </summary>
    public List<SubAssemblyChildDto> Children { get; set; } = [];

    /// <summary>How many of <see cref="Children"/> have not finished — what the order is waiting on.</summary>
    public int LiveChildCount { get; set; }

    public WorkOrderDto() { }
    public WorkOrderDto(WorkOrder workOrder, Shipment? shipment = null)
    {
        Shipment = shipment is null ? null : new ShipmentDto(shipment);
        ParentWorkOrderId = workOrder.ParentWorkOrderId;
        ForComponentId = workOrder.ForComponentId;
        LiveChildCount = workOrder.LiveChildCount;
        Children = workOrder.Children
            .OrderBy(child => child.CreatedUtc)
            .Select(child => new SubAssemblyChildDto(child))
            .ToList();
        Id = workOrder.Id;
        Status = workOrder.CurrentStatus;
        OrderedItemId = workOrder.OrderedItem.ItemId;
        OrderItemQty = workOrder.OrderItemQty;
        Origin = workOrder.Origin;
        PassedQty = workOrder.PassedQty;
        BuildAttempt = workOrder.BuildAttempt;
        Units = workOrder.AssignedStock
            .OrderBy(unit => unit.BuildAttempt)
            .ThenBy(unit => unit.BuiltUtc)
            .Select(unit => new StockUnitDto(unit))
            .ToList();
    }
}

/// <summary>The parcel: who is carrying it, under what number, and which units are in it.</summary>
public class ShipmentDto
{
    public string Carrier { get; set; } = string.Empty;
    public string TrackingNumber { get; set; } = string.Empty;
    public ShipmentStatus Status { get; set; }
    public DateTime BookedUtc { get; set; }
    public DateTime EstimatedArrivalUtc { get; set; }
    public DateTime? DispatchedUtc { get; set; }

    /// <summary>Exactly the units that shipped — the passing ones, never the scrapped.</summary>
    public List<Guid> SerialNumbers { get; set; } = [];

    public ShipmentDto() { }
    public ShipmentDto(Shipment shipment)
    {
        Carrier = shipment.Carrier;
        TrackingNumber = shipment.TrackingNumber;
        Status = shipment.Status;
        BookedUtc = shipment.BookedUtc;
        EstimatedArrivalUtc = shipment.EstimatedArrivalUtc;
        DispatchedUtc = shipment.DispatchedUtc;
        SerialNumbers = shipment.Lines.Select(line => line.SerialNumber).ToList();
    }
}

/// <summary>
/// One sub-assembly order, as its parent sees it (13.3): enough to draw a link and say whether it
/// is still running. Deliberately not a whole <see cref="WorkOrderDto"/> — the child is an ordinary
/// order with its own detail page, and nesting the full graph would make a parent's response carry
/// every unit of every child.
/// </summary>
public class SubAssemblyChildDto
{
    public Guid Id { get; set; }
    public WorkOrderStatus Status { get; set; }

    /// <summary>The component this child is making.</summary>
    public string? ForComponentId { get; set; }

    /// <summary>How many units of that component it was raised to build.</summary>
    public uint Qty { get; set; }

    /// <summary>False once the child is Completed or Cancelled — the parent is no longer waiting on it.</summary>
    public bool IsLive { get; set; }

    public SubAssemblyChildDto() { }
    public SubAssemblyChildDto(WorkOrder child)
    {
        Id = child.Id;
        Status = child.CurrentStatus;
        ForComponentId = child.ForComponentId;
        Qty = child.OrderItemQty;
        IsLive = !child.IsTerminal;
    }
}

/// <summary>One serialized unit and its verdict.</summary>
public class StockUnitDto
{
    public Guid SerialNumber { get; set; }
    public UnitStatus Status { get; set; }
    public int BuildAttempt { get; set; }
    public DateTime BuiltUtc { get; set; }
    public DateTime? InspectedUtc { get; set; }
    public string? ScrapReason { get; set; }

    public StockUnitDto() { }
    public StockUnitDto(StockKeepingUnit unit)
    {
        SerialNumber = unit.SerialNumber;
        Status = unit.Status;
        BuildAttempt = unit.BuildAttempt;
        BuiltUtc = unit.BuiltUtc;
        InspectedUtc = unit.InspectedUtc;
        ScrapReason = unit.ScrapReason;
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