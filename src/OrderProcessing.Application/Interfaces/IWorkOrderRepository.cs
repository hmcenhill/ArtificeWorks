using OrderProcessing.Domain.Models;

namespace OrderProcessing.Application.Interfaces;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> Get(Guid id);
    Task<WorkOrder?> GetWithHistory(Guid id);
    Task<WorkOrder> Add(WorkOrder workOrder);
}
