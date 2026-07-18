using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Interfaces;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> Get(Guid id);
    Task<WorkOrder?> GetWithHistory(Guid id);
    Task<WorkOrder> Add(WorkOrder workOrder);
    Task Update(WorkOrder workOrder);
}
