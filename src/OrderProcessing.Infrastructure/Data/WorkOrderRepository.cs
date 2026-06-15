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

    public async Task<WorkOrder?> Get(string id)
    {
        var workOrder = await _context.WorkOrders.FirstOrDefaultAsync(wo => wo.Id.ToString() == id);
        return workOrder;
    }

    public async Task<WorkOrder> Add(WorkOrder workOrder)
    {
        var createdWorkOrder = await _context.WorkOrders.AddAsync(workOrder);
        await _context.SaveChangesAsync();
        return createdWorkOrder.Entity;
    }
}