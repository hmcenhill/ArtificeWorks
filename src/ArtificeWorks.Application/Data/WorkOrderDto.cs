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

    public WorkOrderDto() { }
    public WorkOrderDto(WorkOrder workOrder, Shipment? shipment = null)
    {
        Shipment = shipment is null ? null : new ShipmentDto(shipment);
        Id = workOrder.Id;
        Status = workOrder.CurrentStatus;
        OrderedItemId = workOrder.OrderedItem.ItemId;
        OrderItemQty = workOrder.OrderItemQty;
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