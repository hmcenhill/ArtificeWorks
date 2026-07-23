using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Application.Data;
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

    public async Task<IReadOnlyList<WorkOrderListItemDto>> List(
        IReadOnlyCollection<WorkOrderStatus> statuses,
        IReadOnlyCollection<WorkOrderOrigin> origins,
        int limit)
    {
        var query = _context.WorkOrders.AsNoTracking();

        // Both filters are optional and repeatable — empty means "no restriction". The IN over the
        // value-converted enum columns translates to the stored names, so this narrows in Postgres
        // rather than in memory.
        if (origins.Count > 0)
        {
            query = query.Where(wo => origins.Contains(wo.Origin));
        }
        if (statuses.Count > 0)
        {
            query = query.Where(wo => statuses.Contains(wo.CurrentStatus));
        }

        // With an explicit status filter the caller has said what it wants, so it is a plain
        // newest-first window. Without one, the default is the bounded live world: in-flight
        // orders sort ahead of terminal ones (the CASE below evaluates false=0 for live,
        // true=1 for Completed/Cancelled), so when `limit` bites it is the older terminal
        // orders that fall off, never a live one.
        var ordered = statuses.Count > 0
            ? query.OrderByDescending(wo => wo.CreatedUtc)
            : query
                .OrderBy(wo => wo.CurrentStatus == WorkOrderStatus.Completed
                            || wo.CurrentStatus == WorkOrderStatus.Cancelled)
                .ThenByDescending(wo => wo.CreatedUtc);

        return await ordered
            .Take(limit)
            .Select(wo => new WorkOrderListItemDto(
                wo.Id,
                wo.OrderedItem.ItemName,
                wo.CurrentStatus,
                wo.Origin,
                wo.CreatedUtc,
                wo.UpdatedUtc))
            .ToListAsync();
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