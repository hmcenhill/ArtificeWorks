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
}