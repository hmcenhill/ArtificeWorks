using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Messaging.Outbox;
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
            // 13.3. Loaded on both read paths, not one: the domain's "a parent cannot complete while
            // it has live children" guard reads this collection, and a guard that silently passes
            // when nobody happened to load the data is not a guard. Only the child rows themselves —
            // their status is on the row, and nothing here needs a child's units or history.
            .Include(wo => wo.Children)
            .FirstOrDefaultAsync(wo => wo.Id == id);
    }

    public async Task<WorkOrder?> GetWithHistory(Guid id)
    {
        return await _context.WorkOrders
            .Include(wo => wo.OrderedItem)
            .Include(wo => wo.StateHistory)
            .Include(wo => wo.AssignedStock)
            .Include(wo => wo.Children)
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
                wo.UpdatedUtc,
                wo.ParentWorkOrderId != null))
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

    public async Task<IReadOnlyList<string>> ListOpenSubAssemblyRequests(
        Guid parentWorkOrderId,
        int parentAttemptNumber,
        CancellationToken cancellationToken = default)
    {
        // "Open" is the same predicate the filtered unique index uses, so the pre-check and the
        // guarantee agree about what they are counting. Anything else would make a race resolve one
        // way in the code and the other way in the database.
        return await _context.WorkOrders
            .AsNoTracking()
            .Where(wo => wo.ParentWorkOrderId == parentWorkOrderId
                && wo.ParentAttemptNumber == parentAttemptNumber
                && wo.CurrentStatus != WorkOrderStatus.Completed
                && wo.CurrentStatus != WorkOrderStatus.Cancelled
                && wo.ForComponentId != null)
            .Select(wo => wo.ForComponentId!)
            .ToListAsync(cancellationToken);
    }

    public async Task Update(WorkOrder workOrder)
    {
        // The work order is loaded and tracked by the same scoped context, so the
        // change tracker already sees the status change and the newly appended
        // history entry (marked Added). Calling DbSet.Update here would instead
        // flag that new entry as Modified and try to UPDATE a nonexistent row, so
        // we just flush the tracked changes.
        //
        // Since 13.3 this also inserts any sub-assembly child orders attached to the parent's
        // Children collection, and the outbox rows announcing them — so a short pick's hold and the
        // work it just scheduled commit as one transaction. If a concurrent delivery spawned the
        // same child first, the filtered unique index rejects this one and the DbUpdateException
        // escapes: that is a *transient* fault by 8.2's classification, the message climbs a rung,
        // and the redelivery finds the open request in the pre-check and holds cleanly. Catching it
        // here to "save the rest" would be theatre — the winner's hold has already moved the
        // parent's xmin, so this context's view of that row is stale either way.
        await _context.SaveChangesAsync();
    }
}