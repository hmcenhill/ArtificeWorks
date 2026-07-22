using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Shipping;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Shipping;

/// <summary>
/// The shipping workflow: book a carrier for the units that passed, then hand the parcel over
/// and close the order out.
/// <para>
/// <strong>Booking transitions nothing.</strong> The work order is already in Delivery when this
/// runs — <c>InspectionService.Resolve</c> advanced it — so the parcel's own progress lives on
/// the <see cref="Shipment"/>, and dispatch is the only thing here that touches the aggregate.
/// Two clocks on one order would have been the easy mistake.
/// </para>
/// <para>
/// <strong>Two ways in, one booking.</strong> The consumer books on
/// <c>work-order.inspection-passed</c>; a visitor books by hand at
/// <c>POST /work-orders/{id}/shipments</c> when <c>Shipping:AutoBook</c> is off. Both land in
/// the same <see cref="Book"/>, so an order shipped by a visitor and one shipped unattended
/// reach identical state — the shape 6.2's verdict endpoint already proved.
/// </para>
/// <para>
/// <strong>Every outcome acks.</strong> A refusal, a duplicate, an order in the wrong state —
/// all handled results. Only an exception nacks.
/// </para>
/// </summary>
public sealed class ShippingService
{
    /// <summary>Author recorded against state-history entries the automatic path writes.</summary>
    public const string Author = "shipping-worker";

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IShipmentRepository _shipmentRepository;
    private readonly ICarrierBooking _carrier;
    private readonly IEventPublisher _eventPublisher;
    private readonly ShippingConfiguration _config;
    private readonly ILogger<ShippingService> _logger;

    public ShippingService(
        IWorkOrderRepository workOrderRepository,
        IShipmentRepository shipmentRepository,
        ICarrierBooking carrier,
        IEventPublisher eventPublisher,
        ShippingConfiguration config,
        ILogger<ShippingService> logger)
    {
        _workOrderRepository = workOrderRepository;
        _shipmentRepository = shipmentRepository;
        _carrier = carrier;
        _eventPublisher = eventPublisher;
        _config = config;
        _logger = logger;
    }

    // ------------------------------------------------------------------ the consumer path

    /// <summary>
    /// Books a carrier for an order whose full quantity has passed — the subscriber Epic 6 left
    /// <c>work-order.inspection-passed</c> waiting for.
    /// </summary>
    /// <param name="serialNumbers">
    /// The passing serials as the event described them. Trusted over re-deriving from the order
    /// so a redelivery would describe the same parcel; empty falls back to the order's own
    /// passing units.
    /// </param>
    public async Task<BookingResult> BookForPassedInspection(
        Guid workOrderId,
        IReadOnlyList<Guid> serialNumbers,
        CancellationToken cancellationToken = default)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Shipping requested for unknown work order {WorkOrderId}.", workOrderId);
            return new BookingResult(BookingOutcome.WorkOrderNotFound, $"No work order found with id {workOrderId}.");
        }

        if (!_config.AutoBook)
        {
            // The order rests in Delivery with no shipment until a visitor picks a carrier. Note
            // it in the history so the wait is visible rather than looking like a stall.
            const string summary = "Awaiting a carrier: automatic booking is switched off.";
            workOrder.AppendNote(Author, summary);
            await _workOrderRepository.Update(workOrder);

            _logger.LogInformation(
                "Auto-booking is off; work order {WorkOrderId} waits in Delivery for a carrier choice.", workOrderId);
            return new BookingResult(BookingOutcome.AwaitingCarrierChoice, summary);
        }

        return await Book(workOrder, serialNumbers, requestedCarrier: null, Author, cancellationToken);
    }

    // ---------------------------------------------------------------------- the API path

    /// <summary>
    /// Books a carrier by hand — the visitor's decision moment, and the same path Epic 12's
    /// failure injection will use rather than a back door of its own. The parcel is derived from
    /// the order's currently passing units, because there is no event to trust here.
    /// </summary>
    public async Task<BookingResult> BookShipment(
        Guid workOrderId,
        string? carrier,
        string bookedBy,
        CancellationToken cancellationToken = default)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            return new BookingResult(BookingOutcome.WorkOrderNotFound, $"No work order found with id {workOrderId}.");
        }

        return await Book(workOrder, serialNumbers: [], carrier, bookedBy, cancellationToken);
    }

    // ------------------------------------------------------------------- shared booking

    private async Task<BookingResult> Book(
        WorkOrder workOrder,
        IReadOnlyList<Guid> serialNumbers,
        string? requestedCarrier,
        string author,
        CancellationToken cancellationToken)
    {
        // Cheap pre-check for the common duplicate case. NOT the guarantee — two deliveries can
        // pass it together — the unique index on shipments.work_order_id is. See TryBook.
        var existing = await _shipmentRepository.GetForWorkOrder(workOrder.Id, cancellationToken);
        if (existing is not null)
        {
            return AlreadyBooked(workOrder.Id, existing);
        }

        if (workOrder.CurrentStatus != WorkOrderStatus.Delivery)
        {
            var summary = $"Work order is {workOrder.CurrentStatus}, not Delivery; nothing is ready to ship.";
            _logger.LogInformation("Shipping rejected for work order {WorkOrderId}: {Summary}", workOrder.Id, summary);
            return new BookingResult(BookingOutcome.NotInDelivery, summary);
        }

        var parcel = serialNumbers.Count > 0 ? serialNumbers : PassedSerials(workOrder);
        if (parcel.Count == 0)
        {
            var summary = $"Work order {workOrder.Id} has no passed units to ship.";
            _logger.LogWarning("Shipping rejected: {Summary}", summary);
            return new BookingResult(BookingOutcome.NothingToShip, summary);
        }

        var booking = _carrier.Book(new CarrierBookingRequest(workOrder.Id, parcel.Count, requestedCarrier));

        return booking.Outcome switch
        {
            CarrierBookingOutcome.Accepted
                => await OnAccepted(workOrder, parcel, booking, author, cancellationToken),
            CarrierBookingOutcome.Refused
                => await OnRefused(workOrder, booking, author),
            _ => new BookingResult(BookingOutcome.UnknownCarrier, booking.Reason!, booking.Carrier)
        };
    }

    private async Task<BookingResult> OnAccepted(
        WorkOrder workOrder,
        IReadOnlyList<Guid> parcel,
        CarrierBookingResult booking,
        string author,
        CancellationToken cancellationToken)
    {
        var shipment = new Shipment(
            workOrder.Id,
            booking.Carrier!,
            booking.TrackingNumber!,
            booking.EstimatedArrivalUtc!.Value,
            parcel);

        var summary = $"Shipment booked: {shipment.Describe()}.";

        // The note is appended to the *tracked* order before the insert, so the shipment, its
        // lines and the history entry all land in one SaveChanges. A losing duplicate therefore
        // does not merely fail to record a marker — it takes its note down with it too. (Picking
        // writes its note separately; here it is free, so it is taken.)
        workOrder.AppendNote(author, ProductionService.Truncate(summary));

        if (!await _shipmentRepository.TryBook(shipment, cancellationToken))
        {
            return AlreadyBooked(workOrder.Id, shipment: null);
        }

        await PublishSafely(new ShipmentScheduled(
            workOrder.Id,
            workOrder.OrderedItem.ItemId,
            shipment.Carrier,
            shipment.TrackingNumber,
            parcel,
            shipment.EstimatedArrivalUtc,
            shipment.BookedUtc), cancellationToken);

        _logger.LogInformation(
            "Work order {WorkOrderId} booked with {Carrier}, tracking {TrackingNumber}, {UnitCount} unit(s), ETA {Eta:O}.",
            workOrder.Id, shipment.Carrier, shipment.TrackingNumber, parcel.Count, shipment.EstimatedArrivalUtc);

        return new BookingResult(BookingOutcome.Booked, summary, shipment.Carrier, shipment.TrackingNumber);
    }

    /// <summary>
    /// The carrier said no (7.3). No shipment row is written — persisting refused bookings would
    /// break the unique index that makes this stage idempotent and turn a clean retry into a
    /// "find the live one" query — so the refusal count lives only in state history. The order
    /// goes OnHold with the reason, and the message acks: no capacity is an external constraint,
    /// the same reading 5.3 took for a material shortage.
    /// </summary>
    private async Task<BookingResult> OnRefused(WorkOrder workOrder, CarrierBookingResult booking, string author)
    {
        var summary = $"Carrier {booking.Carrier} refused the booking: {booking.Reason}";

        var hold = workOrder.SetHold(author, ProductionService.Truncate(summary));
        if (!hold.Success)
        {
            _logger.LogWarning(
                "Work order {WorkOrderId} was refused a carrier but could not be held ({Code}): {Error}",
                workOrder.Id, hold.Code, hold.Error);
        }
        else
        {
            _logger.LogInformation("Work order {WorkOrderId} placed OnHold: {Reason}", workOrder.Id, summary);
        }

        await _workOrderRepository.Update(workOrder);
        return new BookingResult(BookingOutcome.CarrierUnavailable, summary, booking.Carrier);
    }

    // ---------------------------------------------------------------------------- dispatch

    /// <summary>
    /// The pipeline's ending: hand the parcel to the carrier and advance Delivery → Completed.
    /// <para>
    /// Idempotent without a new table. The shipment's own <c>Booked → Dispatched</c> transition
    /// refuses a second hand-over, and belt-and-braces the order's <c>AdvanceToNextStep</c> would
    /// reject a second advance anyway, because Completed is terminal.
    /// </para>
    /// </summary>
    public async Task<DispatchResult> DispatchShipment(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Dispatch requested for unknown work order {WorkOrderId}.", workOrderId);
            return new DispatchResult(DispatchOutcome.WorkOrderNotFound, $"No work order found with id {workOrderId}.");
        }

        var shipment = await _shipmentRepository.GetForWorkOrder(workOrderId, cancellationToken);
        if (shipment is null)
        {
            var summary = $"Work order {workOrderId} has no booked shipment; nothing to dispatch.";
            _logger.LogWarning("Dispatch rejected: {Summary}", summary);
            return new DispatchResult(DispatchOutcome.ShipmentNotFound, summary);
        }

        var dispatched = shipment.Dispatch();
        if (!dispatched.Success)
        {
            return AlreadyDispatched(workOrderId, shipment, dispatched.Error!);
        }

        var serials = shipment.Lines.Select(line => line.SerialNumber).ToList();
        var summaryText = $"Shipment dispatched: {shipment.Describe()}.";

        var advance = workOrder.AdvanceToNextStep(Author, ProductionService.Truncate(summaryText));
        if (!advance.Success)
        {
            // The order can't be completed (held, cancelled, faulted). Leave the shipment booked
            // rather than half-dispatching it: nothing is saved, so the redelivery is clean.
            _logger.LogInformation(
                "Work order {WorkOrderId} could not be completed ({Code}): {Error}",
                workOrderId, advance.Code, advance.Error);
            return new DispatchResult(DispatchOutcome.Rejected, advance.Error!,
                shipment.Carrier, shipment.TrackingNumber);
        }

        // The shipment's status change and the order's transition are both tracked by the same
        // scoped context, so one SaveChanges commits the hand-over and the completion together.
        await _shipmentRepository.Update(cancellationToken);

        await PublishSafely(new WorkOrderCompleted(
            workOrder.Id,
            workOrder.OrderedItem.ItemId,
            shipment.Carrier,
            shipment.TrackingNumber,
            serials,
            shipment.DispatchedUtc!.Value), cancellationToken);

        _logger.LogInformation(
            "Work order {WorkOrderId} completed: {UnitCount} unit(s) dispatched with {Carrier}, tracking {TrackingNumber}.",
            workOrderId, serials.Count, shipment.Carrier, shipment.TrackingNumber);

        return new DispatchResult(DispatchOutcome.Dispatched, summaryText, shipment.Carrier, shipment.TrackingNumber);
    }

    // ----------------------------------------------------------------------------- helpers

    /// <summary>The order's currently passing serials — the parcel when no event named one.</summary>
    private static List<Guid> PassedSerials(WorkOrder workOrder) =>
        workOrder.AssignedStock
            .Where(unit => unit.Status == UnitStatus.Passed)
            .Select(unit => unit.SerialNumber)
            .ToList();

    /// <summary>
    /// A redelivery. Nothing is written — deliberately not even a state-history note, since a
    /// note per redelivery would itself be a non-idempotent side effect — but it IS logged, so
    /// idempotency stays observable when Epic 12 redelivers a message in front of an audience.
    /// </summary>
    private BookingResult AlreadyBooked(Guid workOrderId, Shipment? shipment)
    {
        var summary = shipment is null
            ? $"Work order {workOrderId} was booked concurrently by another delivery; skipping duplicate."
            : $"Work order {workOrderId} was already booked with {shipment.Carrier} at {shipment.BookedUtc:O}; skipping duplicate.";

        _logger.LogInformation("Duplicate booking skipped (idempotent): {Summary}", summary);
        return new BookingResult(BookingOutcome.AlreadyBooked, summary, shipment?.Carrier, shipment?.TrackingNumber);
    }

    private DispatchResult AlreadyDispatched(Guid workOrderId, Shipment shipment, string error)
    {
        _logger.LogInformation(
            "Duplicate dispatch skipped (idempotent) for work order {WorkOrderId}: {Error}", workOrderId, error);
        return new DispatchResult(DispatchOutcome.AlreadyDispatched, error, shipment.Carrier, shipment.TrackingNumber);
    }

    /// <summary>Best-effort publish; see <see cref="ProductionService"/> for the reasoning.</summary>
    private async Task PublishSafely<T>(T @event, CancellationToken cancellationToken) where T : IntegrationEvent
    {
        try
        {
            await _eventPublisher.PublishAsync(@event, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Failed to publish {EventType}; the shipment is committed but the event was dropped.",
                @event.EventType);
        }
    }
}
