using OrderProcessing.Domain.Models;

namespace OrderProcessing.Application.Interfaces;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> Get(string id);
    Task<WorkOrder> Add(WorkOrder workOrder);
}
