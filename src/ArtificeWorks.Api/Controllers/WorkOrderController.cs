using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Handlers;

namespace ArtificeWorks.Api.Controllers;

[ApiController]
[Route("work-orders")]
public class WorkOrderController(WorkOrderHandler workOrderHandler) : ControllerBase
{
    private readonly WorkOrderHandler _workOrderHandler = workOrderHandler;

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkOrderDto>> Get(Guid id)
    {
        var workOrder = await _workOrderHandler.GetWorkOrder(id);
        return workOrder is null ? NotFound() : Ok(workOrder);
    }

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<WorkOrderHistoryDto>> GetHistory(Guid id)
    {
        var history = await _workOrderHandler.GetWorkOrderHistory(id);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpPost]
    public async Task<ActionResult<WorkOrderDto>> Create([FromBody] CreateWorkOrderRequest request)
    {
        var response = await _workOrderHandler.CreateWorkOrder(request);
        return response.IsSuccess
            ? Created($"/work-orders/{response.WorkOrder!.Id}", response.WorkOrder)
            : BadRequest(response.Error);
    }

    [HttpPost("{id:guid}/advance")]
    public async Task<ActionResult<WorkOrderDto>> Advance(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.AdvanceWorkOrder(id, request));

    [HttpPost("{id:guid}/hold")]
    public async Task<ActionResult<WorkOrderDto>> Hold(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.HoldWorkOrder(id, request));

    [HttpPost("{id:guid}/release")]
    public async Task<ActionResult<WorkOrderDto>> Release(Guid id, [FromBody] WorkOrderCommandRequest request)
        => MapCommandResult(await _workOrderHandler.ReleaseWorkOrder(id, request));

    private ActionResult<WorkOrderDto> MapCommandResult(WorkOrderCommandResponse response) => response.Outcome switch
    {
        WorkOrderCommandOutcome.Success => Ok(response.WorkOrder),
        WorkOrderCommandOutcome.NotFound => NotFound(response.Error),
        // Rejected transition: the request conflicts with the order's current state.
        // 3.3 will formalise this as a ProblemDetails payload.
        _ => Conflict(response.Error)
    };
}