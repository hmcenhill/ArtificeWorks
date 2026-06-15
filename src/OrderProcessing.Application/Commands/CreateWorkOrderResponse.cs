using OrderProcessing.Application.Data;
using OrderProcessing.Domain.Models;

namespace OrderProcessing.Application.Commands;

public class CreateWorkOrderResponse
{
    public bool IsSuccess { get; init; }
    public WorkOrderDto? WorkOrder { get; init; }
    public string? Error { get; init; }
}
