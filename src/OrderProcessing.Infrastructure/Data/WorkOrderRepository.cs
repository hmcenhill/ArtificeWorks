using Microsoft.EntityFrameworkCore;

using OrderProcessing.Application.Interfaces;
using OrderProcessing.Domain.Models;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Infrastructure.Data;

public class WorkOrderRepository : IWorkOrderRepository
{
    private readonly WorkOrderProcessingDbContext _context;

    public WorkOrderRepository(WorkOrderProcessingDbContext context)
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