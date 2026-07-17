using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Persistence;

namespace ArtificeWorks.Infrastructure.Data;

public class WorkOrderRepository : IWorkOrderRepository
{
    private readonly ArtificeWorksDbContext _context;

    public WorkOrderRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<WorkOrder?> Get(Guid id)
    {
        return await _context.WorkOrders
            .Include(wo => wo.OrderedItem)
            .FirstOrDefaultAsync(wo => wo.Id == id);
    }

    public async Task<WorkOrder?> GetWithHistory(Guid id)
    {
        return await _context.WorkOrders
            .Include(wo => wo.OrderedItem)
            .Include(wo => wo.StateHistory)
            .Include(wo => wo.AssignedStock)
            .FirstOrDefaultAsync(wo => wo.Id == id);
    }

    public async Task<WorkOrder> Add(WorkOrder workOrder)
    {
        var createdWorkOrder = await _context.WorkOrders.AddAsync(workOrder);
        await _context.SaveChangesAsync();
        return createdWorkOrder.Entity;
    }
}