using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Handlers;

namespace ArtificeWorks.Api.Controllers;

[ApiController]
[Route("work-orders")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class WorkOrderController(WorkOrderHandler workOrderHandler) : ApiControllerBase
{
    private readonly WorkOrderHandler _workOrderHandler = workOrderHandler;

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

    [HttpPost]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status201Created)]
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
