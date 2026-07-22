namespace ArtificeWorks.Domain.Models.Shipping;

/// <summary>
/// The parcel: which units left the factory, with whom, under what tracking number.
/// <para>
/// Its own aggregate next to <see cref="Materials.MaterialReservation"/>, for the same reason —
/// the work order tracks <em>manufacturing</em>, and a shipment tracks <em>logistics</em>. The
/// order is already in Delivery when a shipment is booked (inspection advanced it), so booking
/// transitions nothing on the aggregate. Adding a manufacturing status for a logistics fact
/// would put two clocks on one order.
/// </para>
/// <para>
/// It is also this epic's idempotency key. Shipping happens exactly once per order, so Epic 5's
/// "the record <em>is</em> the dedupe key" applies unchanged: a unique index on
/// <see cref="WorkOrderId"/> means a redelivered <c>InspectionPassed</c> collides on insert.
/// 6.4 argued the key must follow the thing that must happen once — for production that was an
/// attempt; here it is the order again, and that is not a regression.
/// </para>
/// </summary>
public class Shipment
{
    public Guid Id { get; }
    public Guid WorkOrderId { get; }

    /// <summary>The carrier that accepted the job — one of the configured virtual carriers.</summary>
    public string Carrier { get; } = string.Empty;

    public string TrackingNumber { get; } = string.Empty;

    public DateTime BookedUtc { get; }

    /// <summary>Virtual, derived at booking from <c>Shipping:TransitDays</c>. Nothing enforces it.</summary>
    public DateTime EstimatedArrivalUtc { get; }

    /// <summary>When the parcel was handed over. Null while the shipment is only <see cref="ShipmentStatus.Booked"/>.</summary>
    public DateTime? DispatchedUtc { get; private set; }

    public ShipmentStatus Status { get; private set; }

    /// <summary>The serialized units in the parcel. Scrapped serials never appear here.</summary>
    public IReadOnlyList<ShipmentLine> Lines => _lines.AsReadOnly();
    private readonly List<ShipmentLine> _lines = new();

    private Shipment() { }

    public Shipment(
        Guid workOrderId,
        string carrier,
        string trackingNumber,
        DateTime estimatedArrivalUtc,
        IEnumerable<Guid> serialNumbers)
    {
        if (string.IsNullOrWhiteSpace(carrier))
        {
            throw new ArgumentException("A shipment must name a carrier.", nameof(carrier));
        }
        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            throw new ArgumentException("A shipment must carry a tracking number.", nameof(trackingNumber));
        }

        Id = Guid.NewGuid();
        WorkOrderId = workOrderId;
        Carrier = carrier;
        TrackingNumber = trackingNumber;
        BookedUtc = DateTime.UtcNow;
        EstimatedArrivalUtc = estimatedArrivalUtc;
        Status = ShipmentStatus.Booked;

        foreach (var serialNumber in serialNumbers)
        {
            _lines.Add(new ShipmentLine(this, serialNumber));
        }

        if (_lines.Count == 0)
        {
            throw new ArgumentException("A shipment must contain at least one unit.", nameof(serialNumbers));
        }
    }

    /// <summary>
    /// Hands the parcel to the carrier. <strong>This is dispatch's whole idempotency story</strong>
    /// (7.2): a redelivered <c>ShipmentScheduled</c> finds the shipment already Dispatched and is
    /// refused here, so no run table is needed. Belt and braces, the order's own
    /// <c>AdvanceToNextStep</c> would reject a second advance too, because Completed is terminal.
    /// </summary>
    public TransitionResult Dispatch()
    {
        if (Status == ShipmentStatus.Dispatched)
        {
            return TransitionResult.Rejected(TransitionErrorCode.InvalidTransition,
                $"Shipment {TrackingNumber} was already dispatched at {DispatchedUtc:O}.");
        }
        if (Status == ShipmentStatus.Cancelled)
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Shipment {TrackingNumber} was voided and cannot be dispatched.");
        }

        Status = ShipmentStatus.Dispatched;
        DispatchedUtc = DateTime.UtcNow;
        return TransitionResult.Ok();
    }

    /// <summary>
    /// Voids a booked parcel because its work order was cancelled. Decided at 7.2 rather than
    /// left to be discovered later: a cancelled order should not leave a live parcel.
    /// </summary>
    public TransitionResult Void()
    {
        if (Status == ShipmentStatus.Dispatched)
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Shipment {TrackingNumber} has already been dispatched and cannot be voided.");
        }

        Status = ShipmentStatus.Cancelled;
        return TransitionResult.Ok();
    }

    /// <summary>Human-readable summary for the work order's state history.</summary>
    public string Describe() =>
        $"{_lines.Count} unit(s) with {Carrier}, tracking {TrackingNumber}, ETA {EstimatedArrivalUtc:yyyy-MM-dd}";
}
