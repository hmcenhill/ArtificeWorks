namespace ArtificeWorks.Application.Shipping;

/// <summary>How a booking attempt ended.</summary>
public enum BookingOutcome
{
    /// <summary>A carrier accepted; the shipment exists and <c>ShipmentScheduled</c> is out.</summary>
    Booked,

    /// <summary>
    /// The carrier refused. No shipment row, the order is OnHold with the reason, and the
    /// message acks — waiting on a carrier is a result, not an error (7.3).
    /// </summary>
    CarrierUnavailable,

    /// <summary>A visitor named a carrier this factory does not work with. Nothing was attempted.</summary>
    UnknownCarrier,

    /// <summary>The order already has a shipment — a duplicate delivery, safely ignored.</summary>
    AlreadyBooked,

    /// <summary>
    /// <c>Shipping:AutoBook</c> is off, so the order waits in Delivery for a visitor to choose a
    /// carrier. Only the consumer path can produce this.
    /// </summary>
    AwaitingCarrierChoice,

    /// <summary>No work order with that id.</summary>
    WorkOrderNotFound,

    /// <summary>The order is not in Delivery, so there is nothing to ship yet.</summary>
    NotInDelivery,

    /// <summary>The order is in Delivery but has no passed units to put in a parcel.</summary>
    NothingToShip
}

/// <param name="Summary">Human-readable description of what happened, as written to state history.</param>
public sealed record BookingResult(
    BookingOutcome Outcome,
    string Summary,
    string? Carrier = null,
    string? TrackingNumber = null);

/// <summary>How a dispatch attempt ended.</summary>
public enum DispatchOutcome
{
    /// <summary>The parcel is with the carrier and the order is Completed.</summary>
    Dispatched,

    /// <summary>
    /// The shipment was already dispatched — a duplicate delivery. The shipment's own
    /// <c>Booked → Dispatched</c> transition is the guard; no run table needed (7.2).
    /// </summary>
    AlreadyDispatched,

    /// <summary>No work order with that id.</summary>
    WorkOrderNotFound,

    /// <summary>No shipment booked for this order; nothing to hand over.</summary>
    ShipmentNotFound,

    /// <summary>The order could not be completed in its current state (held, cancelled, faulted).</summary>
    Rejected
}

public sealed record DispatchResult(
    DispatchOutcome Outcome,
    string Summary,
    string? Carrier = null,
    string? TrackingNumber = null);
