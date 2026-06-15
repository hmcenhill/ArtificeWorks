using System.Net;

using OrderProcessing.Domain.Models;

namespace OrderProcessing.Application.Data;

public class WorkOrderDto
{
    public Guid Id { get; set; }
    public WorkOrderStatus Status { get; set; }

    public WorkOrderDto() { }
    public WorkOrderDto(WorkOrder workOrder)
    {
        Id = workOrder.Id;
        Status = workOrder.CurrentStatus;
    }
}