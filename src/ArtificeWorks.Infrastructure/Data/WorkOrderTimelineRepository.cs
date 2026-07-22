using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Data;

/// <summary>
/// Gathers a work order's whole story from the five places it is already recorded. Everything is
/// <c>AsNoTracking</c>: the timeline is a narrative, and nothing composed here should ever be a
/// candidate for a write.
/// </summary>
public class WorkOrderTimelineRepository : IWorkOrderTimelineRepository
{
    private readonly ArtificeWorksDbContext _context;

    public WorkOrderTimelineRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<TimelineData?> GetTimelineData(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        var workOrder = await _context.WorkOrders
            .AsNoTracking()
            .Include(order => order.OrderedItem)
            .Include(order => order.StateHistory)
            .Include(order => order.AssignedStock)
            .FirstOrDefaultAsync(order => order.Id == workOrderId, cancellationToken);

        if (workOrder is null)
        {
            return null;
        }

        var reservation = await _context.MaterialReservations
            .AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.WorkOrderId == workOrderId, cancellationToken);

        var productionRuns = await _context.ProductionRuns
            .AsNoTracking()
            .Where(run => run.WorkOrderId == workOrderId)
            .OrderBy(run => run.AttemptNumber)
            .ToListAsync(cancellationToken);

        var inspectionRuns = await _context.InspectionRuns
            .AsNoTracking()
            .Where(run => run.WorkOrderId == workOrderId)
            .OrderBy(run => run.AttemptNumber)
            .ToListAsync(cancellationToken);

        var shipment = await _context.Shipments
            .AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.WorkOrderId == workOrderId, cancellationToken);

        return new TimelineData(
            workOrder,
            reservation,
            productionRuns,
            inspectionRuns,
            shipment);
    }
}
