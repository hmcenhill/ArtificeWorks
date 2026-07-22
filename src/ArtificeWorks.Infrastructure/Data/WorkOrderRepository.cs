using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Persistence;

using Npgsql;

namespace ArtificeWorks.Infrastructure.Data;

public class WorkOrderRepository : IWorkOrderRepository
{
    private const string UniqueViolation = "23505";

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

        try
        {
            // This SaveChanges is doing more than it looks since 8.1: it also flushes the outbox
            // row the handler staged, and (when the request carried one) 8.4's idempotency key.
            // Work, announcement and marker commit atomically or not at all.
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new DuplicateKeyException(
                "A unique constraint rejected this write; the caller is expected to resolve it.", e);
        }

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