using Microsoft.AspNetCore.Mvc;

using OrderProcessing.Application.Commands;
using OrderProcessing.Application.Data;
using OrderProcessing.Application.Handlers;

namespace OrderProcessing.Api.Controllers;

[ApiController]
[Route("work-orders")]
public class WorkOrderController : ControllerBase
{
    private readonly WorkOrderHandler _workOrderHandler;


    public WorkOrderController(WorkOrderHandler workOrderHandler)
    {
        _workOrderHandler = workOrderHandler;
    }

    [HttpPost]
    public async Task<ActionResult<WorkOrderDto>> Create([FromBody] CreateWorkOrderRequest request)
    {
        var response = await _workOrderHandler.CreateWorkOrder(request);
        if (response.IsSuccess)
        {
            return Created($"/work-orders/{response.WorkOrder!.Id}", response.WorkOrder);
        }
        return BadRequest(response.Error);
    }
}