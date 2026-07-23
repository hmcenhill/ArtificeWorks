using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Api.Middleware;
using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Handlers;
using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Api.Controllers;

[ApiController]
[Route("work-orders")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class WorkOrderController(
    WorkOrderHandler workOrderHandler,
    InspectionService inspection,
    ShippingService shipping) : ApiControllerBase
{
    private readonly WorkOrderHandler _workOrderHandler = workOrderHandler;
    private readonly InspectionService _inspection = inspection;
    private readonly ShippingService _shipping = shipping;

    /// <summary>
    /// The factory's current orders as a slim list, for the board (11.1). Filterable by
    /// <c>status</c> and <c>origin</c> — both optional and repeatable, so
    /// <c>?status=Inspection&amp;status=Delivery&amp;origin=Visitor</c> narrows to visitor orders
    /// in either stage.
    /// <para>
    /// Bounded and newest-first by construction. With no <c>status</c> filter the result is the
    /// bounded live world — every in-flight order plus a capped window of the most-recently
    /// finished ones — not an ever-growing history; that is a report, and this is a live board.
    /// <c>limit</c> caps the window (default <see cref="WorkOrderHandler.DefaultListLimit"/>,
    /// ceiling <see cref="WorkOrderHandler.MaxListLimit"/>).
    /// </para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<WorkOrderListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WorkOrderListItemDto>>> List(
        [FromQuery(Name = "status")] WorkOrderStatus[]? status,
        [FromQuery(Name = "origin")] WorkOrderOrigin[]? origin,
        [FromQuery] int? limit)
        => Ok(await _workOrderHandler.ListWorkOrders(
            status ?? [], origin ?? [], limit));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderDto>> Get(Guid id)
    {
        var workOrder = await _workOrderHandler.GetWorkOrder(id);
        return workOrder is null
            ? Problem(StatusCodes.Status404NotFound, ProblemCodes.WorkOrderNotFound, $"No work order found with id: {id}")
            : Ok(workOrder);
    }

    [HttpGet("{id:guid}/history")]
    [ProducesResponseType<WorkOrderHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderHistoryDto>> GetHistory(Guid id)
    {
        var history = await _workOrderHandler.GetWorkOrderHistory(id);
        return history is null
            ? Problem(StatusCodes.Status404NotFound, ProblemCodes.WorkOrderNotFound, $"No work order found with id: {id}")
            : Ok(history);
    }

    /// <summary>
    /// The work order's whole story in one chronological list: state changes, the material pick,
    /// each build attempt, each inspection and its per-unit verdicts, the booking and the
    /// dispatch. Entries are typed by <c>kind</c> so a client switches on it rather than
    /// reconciling five read models.
    /// <para>
    /// This is <em>what happened</em>, derived from the records the system keeps — it is not the
    /// message log, and nothing in it proves a message flowed. <c>/history</c> stays as the raw
    /// state log; this is the composed narrative.
    /// </para>
    /// </summary>
    [HttpGet("{id:guid}/timeline")]
    [ProducesResponseType<WorkOrderTimelineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderTimelineDto>> GetTimeline(Guid id, CancellationToken cancellationToken)
    {
        var timeline = await _workOrderHandler.GetWorkOrderTimeline(id, cancellationToken);
        return timeline is null
            ? Problem(StatusCodes.Status404NotFound, ProblemCodes.WorkOrderNotFound, $"No work order found with id: {id}")
            : Ok(timeline);
    }

    /// <summary>
    /// Creates a work order.
    /// <para>
    /// <strong>Optional <c>Idempotency-Key</c> header (8.4).</strong> Send one and a repeat of the
    /// same request — a double-click, a flaky connection, a proxy replaying a request — returns
    /// the original <c>201</c> and its work order rather than creating a second one; the replay
    /// is marked with an <c>Idempotency-Replayed: true</c> response header. Omit it and nothing
    /// changes: the header is opt-in, and a visitor with curl need not know it exists.
    /// </para>
    /// <para>
    /// Reusing a key with a <em>different</em> body is 422 <c>idempotency_key_reused</c> — a
    /// client bug, and one worth being told about rather than having the first response quietly
    /// replayed over it.
    /// </para>
    /// </summary>
    [HttpPost]
    [Idempotent]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<WorkOrderDto>> Create([FromBody] CreateWorkOrderRequest request)
    {
        var response = await _workOrderHandler.CreateWorkOrder(request);
        return response.Outcome switch
        {
            CreateWorkOrderOutcome.Success => Created($"/work-orders/{response.WorkOrder!.Id}", response.WorkOrder),
            // The referenced product does not exist: the request body is invalid (400),
            // distinct from a work order of its own that is missing (404).
            CreateWorkOrderOutcome.ProductNotFound
                => Problem(StatusCodes.Status400BadRequest, ProblemCodes.ProductNotFound, response.Error!),
            _ => Problem(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError,
                "The work order could not be saved.")
        };
    }

    [HttpPost("{id:guid}/advance")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderDto>> Advance(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.AdvanceWorkOrder(id, request));

    [HttpPost("{id:guid}/hold")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderDto>> Hold(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.HoldWorkOrder(id, request));

    [HttpPost("{id:guid}/release")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderDto>> Release(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.ReleaseWorkOrder(id, request));

    /// <summary>
    /// Cancels a work order. Cancellation is terminal and releases any stock the
    /// order had assigned. It is allowed from any state except the terminal ones
    /// (<c>Completed</c> and <c>Cancelled</c>); attempting to cancel those returns
    /// 409 Conflict with reason code <c>terminal_state</c>. A faulted order can
    /// still be cancelled — it is an escape hatch out of the stuck Fault state.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderDto>> Cancel(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.CancelWorkOrder(id, request));

    /// <summary>
    /// Records an inspection verdict for one serialized unit by hand — the alternative to the
    /// automatic inspector, and the same path Epic 12's failure injection will use rather than
    /// a back door of its own.
    /// <para>
    /// The order must be in Inspection (409 <c>order_not_in_inspection</c>), the serial number
    /// must belong to it (404 <c>unit_not_found</c>), and a unit may only be judged once
    /// (409 <c>unit_already_inspected</c> — the clean resolution when the auto-inspector got
    /// there first). A verdict that completes the attempt resolves the order exactly as the
    /// automatic path does: to Delivery, back to production, or to Fault.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/inspections")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderDto>> RecordVerdict(Guid id, [FromBody] RecordVerdictRequest request)
    {
        var result = await _inspection.RecordVerdict(
            id, request.SerialNumber, request.Passed, request.Reason, request.CreatedBy);

        return result.Outcome switch
        {
            VerdictOutcome.Recorded => Ok(await _workOrderHandler.GetWorkOrder(id)),
            VerdictOutcome.WorkOrderNotFound
                => Problem(StatusCodes.Status404NotFound, ProblemCodes.WorkOrderNotFound, result.Summary),
            VerdictOutcome.UnitNotFound
                => Problem(StatusCodes.Status404NotFound, ProblemCodes.UnitNotFound, result.Summary),
            VerdictOutcome.NotInInspection
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.OrderNotInInspection, result.Summary),
            VerdictOutcome.AlreadyInspected
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.UnitAlreadyInspected, result.Summary),
            // A missing scrap reason is a malformed request, not a state conflict.
            _ => Problem(StatusCodes.Status400BadRequest, ProblemCodes.ScrapReasonRequired, result.Summary)
        };
    }

    /// <summary>
    /// Books a carrier for an order whose units have passed — the visitor's decision moment,
    /// reachable when <c>Shipping:AutoBook</c> is off and the order is resting in Delivery.
    /// <para>
    /// It calls the <em>same</em> booking workflow the shipping consumer calls, so an order
    /// shipped by a visitor and one shipped unattended reach identical state — the shape 6.2's
    /// verdict endpoint already proved. Once booked, dispatch and completion follow over the bus
    /// with no further input: the decision offered here is <em>which carrier</em>, not
    /// <em>whether to finish</em>.
    /// </para>
    /// <para>
    /// Problem codes: <c>order_not_in_delivery</c> (409), <c>shipment_already_booked</c> (409),
    /// <c>unknown_carrier</c> (400 — the caller's mistake) and <c>carrier_unavailable</c>
    /// (409 — the carrier exists, it just won't take the job).
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/shipments")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderDto>> BookShipment(
        Guid id,
        [FromBody] BookShipmentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _shipping.BookShipment(id, request.Carrier, request.CreatedBy, cancellationToken);

        return result.Outcome switch
        {
            BookingOutcome.Booked => Ok(await _workOrderHandler.GetWorkOrder(id)),
            BookingOutcome.WorkOrderNotFound
                => Problem(StatusCodes.Status404NotFound, ProblemCodes.WorkOrderNotFound, result.Summary),
            BookingOutcome.NotInDelivery
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.OrderNotInDelivery, result.Summary),
            BookingOutcome.AlreadyBooked
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.ShipmentAlreadyBooked, result.Summary),
            BookingOutcome.CarrierUnavailable
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.CarrierUnavailable, result.Summary),
            BookingOutcome.NothingToShip
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.NothingToShip, result.Summary),
            // A carrier this factory doesn't work with is a malformed request, not a conflict.
            _ => Problem(StatusCodes.Status400BadRequest, ProblemCodes.UnknownCarrier, result.Summary)
        };
    }

    private ActionResult<WorkOrderDto> MapCommandResult(WorkOrderCommandResponse response) => response.Outcome switch
    {
        WorkOrderCommandOutcome.Success => Ok(response.WorkOrder),
        WorkOrderCommandOutcome.NotFound
            => Problem(StatusCodes.Status404NotFound, ProblemCodes.WorkOrderNotFound, response.Error!),
        // Rejected transition: the request conflicts with the order's current state.
        _ => Problem(StatusCodes.Status409Conflict,
            ProblemCodes.ForTransition(response.ReasonCode!.Value), response.Error!)
    };
}
