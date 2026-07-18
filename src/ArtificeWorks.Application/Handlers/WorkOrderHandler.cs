using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Handlers;

public class WorkOrderHandler
{
    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IProductRepository _productRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<WorkOrderHandler> _logger;

    public WorkOrderHandler(IWorkOrderRepository workOrderRepository,
        IProductRepository productRepository,
        IEventPublisher eventPublisher,
        ILogger<WorkOrderHandler> logger)
    {
        _workOrderRepository = workOrderRepository;
        _productRepository = productRepository;
        _eventPublisher = eventPublisher;
        _logger = logger;
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
        => ExecuteCommand(id,
            wo => wo.AdvanceToNextStep(request.CreatedBy, request.Notes),
            onCommitted: PublishAdvanceEvents);

    public Task<WorkOrderCommandResponse> HoldWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.SetHold(request.CreatedBy, request.Notes));

    public Task<WorkOrderCommandResponse> ReleaseWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.ReleaseHold(request.CreatedBy, request.Notes));

    public Task<WorkOrderCommandResponse> CancelWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.Cancel(request.CreatedBy, request.Notes));

    private async Task<WorkOrderCommandResponse> ExecuteCommand(Guid id,
        Func<WorkOrder, TransitionResult> command,
        Func<WorkOrder, Task>? onCommitted = null)
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
                ReasonCode = result.Code,
                Error = result.Error
            };
        }

        await _workOrderRepository.Update(workOrder);

        // Publish-after-commit: the transition is persisted (source of truth) before any
        // event goes out. Publishing is best-effort — see PublishSafely.
        if (onCommitted is not null)
        {
            await onCommitted(workOrder);
        }

        return new WorkOrderCommandResponse
        {
            Outcome = WorkOrderCommandOutcome.Success,
            WorkOrder = new WorkOrderDto(workOrder)
        };
    }

    /// <summary>
    /// Advancing has no dedicated per-step event yet; the first async slice only cares
    /// about Intake → Scheduled. Later workflow epics add the remaining transitions.
    /// </summary>
    private Task PublishAdvanceEvents(WorkOrder workOrder)
    {
        if (workOrder.CurrentStatus == WorkOrderStatus.Scheduled)
        {
            return PublishSafely(new WorkOrderScheduled(
                workOrder.Id,
                workOrder.OrderedItem.ItemId,
                workOrder.OrderedItem.ItemName,
                workOrder.OrderItemQty,
                workOrder.UpdatedUtc));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes an event without letting a broker outage fail the request whose state
    /// change already committed. This is an at-most-once gap by design; Epic 8's outbox
    /// makes delivery reliable.
    /// </summary>
    private async Task PublishSafely<T>(T @event) where T : Messaging.IntegrationEvent
    {
        try
        {
            await _eventPublisher.PublishAsync(@event);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to publish {EventType}; state change is committed but the event was dropped.", @event.EventType);
        }
    }

    public async Task<CreateWorkOrderResponse> CreateWorkOrder(CreateWorkOrderRequest request)
    {
        var product = await _productRepository.Get(request.ItemId);
        if (product is null)
        {
            return new CreateWorkOrderResponse
            {
                Outcome = CreateWorkOrderOutcome.ProductNotFound,
                Error = $"No product found with id: {request.ItemId}"
            };
        }

        var newOrder = new WorkOrder(request.Requestor, product, request.Qty, request.Notes);
        try
        {
            var savedWorkOrder = await _workOrderRepository.Add(newOrder);
            if (savedWorkOrder is not null)
            {
                await PublishSafely(new WorkOrderCreated(
                    savedWorkOrder.Id,
                    savedWorkOrder.OrderedItem.ItemId,
                    savedWorkOrder.OrderedItem.ItemName,
                    savedWorkOrder.OrderItemQty,
                    request.Requestor,
                    savedWorkOrder.CreatedUtc));

                return new CreateWorkOrderResponse
                {
                    Outcome = CreateWorkOrderOutcome.Success,
                    WorkOrder = new WorkOrderDto(savedWorkOrder)
                };
            }
            return new CreateWorkOrderResponse
            {
                Outcome = CreateWorkOrderOutcome.Error,
                Error = "Save action returned no response"
            };
        }
        catch (Exception e)
        {
            return new CreateWorkOrderResponse
            {
                Outcome = CreateWorkOrderOutcome.Error,
                Error = e.Message
            };
        }
    }
}
