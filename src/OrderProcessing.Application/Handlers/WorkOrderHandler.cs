using OrderProcessing.Application.Commands;
using OrderProcessing.Application.Data;
using OrderProcessing.Application.Interfaces;
using OrderProcessing.Domain.Models;

namespace OrderProcessing.Application.Handlers;

public class WorkOrderHandler
{
    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IProductRepository _productRepository;

    public WorkOrderHandler(IWorkOrderRepository workOrderRepository,
        IProductRepository productRepository)
    {
        _workOrderRepository = workOrderRepository;
        _productRepository = productRepository;
    }

    public async Task<WorkOrderDto?> GetWorkOrder(Guid id)
    {
        var workOrder = await _workOrderRepository.Get(id);
        return workOrder is null ? null : new WorkOrderDto(workOrder);
    }

    public async Task<WorkOrderHistoryDto?> GetWorkOrderHistory(Guid id)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(id);
        return workOrder is null ? null : new WorkOrderHistoryDto(workOrder);
    }

    public async Task<CreateWorkOrderResponse> CreateWorkOrder(CreateWorkOrderRequest request)
    {
        var product = await _productRepository.Get(request.ItemId);
        var errors = string.Empty;
        if (product is null)
        {
            errors = $"Error creating work order: no product found with id: {request.ItemId}";
        }
        else
        {
            var newOrder = new WorkOrder(request.Requestor, product, request.Qty, request.Notes);
            try
            {
                var savedWorkOrder = await _workOrderRepository.Add(newOrder);
                if (savedWorkOrder is not null)
                {
                    return new CreateWorkOrderResponse
                    {
                        IsSuccess = true,
                        WorkOrder = new WorkOrderDto(savedWorkOrder)
                    };
                }
                errors = "Save action returned no response";
            }
            catch (Exception e)
            {
                errors = e.Message;
            }
        }
        return new CreateWorkOrderResponse
        {
            IsSuccess = false,
            Error = errors
        };
    }
}
