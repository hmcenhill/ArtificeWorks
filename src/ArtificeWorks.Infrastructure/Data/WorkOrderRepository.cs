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
            // The serialized units and their verdicts are part of the work order's read model
            // since 6.2 — a failed inspection has to be visible on the API, not only in the log.
            .Include(wo => wo.AssignedStock)
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

    public async Task Update(WorkOrder workOrder)
    {
        // The work order is loaded and tracked by the same scoped context, so the
        // change tracker already sees the status change and the newly appended
        // history entry (marked Added). Calling DbSet.Update here would instead
        // flag that new entry as Modified and try to UPDATE a nonexistent row, so
        // we just flush the tracked changes.
        await _context.SaveChangesAsync();
    }
}