using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Handlers;

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

    public Task<WorkOrderCommandResponse> AdvanceWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.AdvanceToNextStep(request.CreatedBy, request.Notes));

    public Task<WorkOrderCommandResponse> HoldWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.SetHold(request.CreatedBy, request.Notes));

    public Task<WorkOrderCommandResponse> ReleaseWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.ReleaseHold(request.CreatedBy, request.Notes));

    private async Task<WorkOrderCommandResponse> ExecuteCommand(Guid id, Func<WorkOrder, TransitionResult> command)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(id);
        if (workOrder is null)
        {
            return new WorkOrderCommandResponse
            {
                Outcome = WorkOrderCommandOutcome.NotFound,
                Error = $"No work order found with id: {id}"
            };
        }

        var result = command(workOrder);
        if (!result.Success)
        {
            return new WorkOrderCommandResponse
            {
                Outcome = WorkOrderCommandOutcome.Rejected,
                Error = result.Error
            };
        }

        await _workOrderRepository.Update(workOrder);
        return new WorkOrderCommandResponse
        {
            Outcome = WorkOrderCommandOutcome.Success,
            WorkOrder = new WorkOrderDto(workOrder)
        };
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
