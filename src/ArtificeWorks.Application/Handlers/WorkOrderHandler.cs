using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Handlers;

public class WorkOrderHandler
{
    /// <summary>Author recorded against state-history entries this handler writes on its own initiative.</summary>
    public const string Author = "work-order-api";

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IProductRepository _productRepository;
    private readonly IShipmentRepository _shipmentRepository;
    private readonly IWorkOrderTimelineRepository _timelineRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<WorkOrderHandler> _logger;

    public WorkOrderHandler(IWorkOrderRepository workOrderRepository,
        IProductRepository productRepository,
        IShipmentRepository shipmentRepository,
        IWorkOrderTimelineRepository timelineRepository,
        IEventPublisher eventPublisher,
        ILogger<WorkOrderHandler> logger)
    {
        _workOrderRepository = workOrderRepository;
        _productRepository = productRepository;
        _shipmentRepository = shipmentRepository;
        _timelineRepository = timelineRepository;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<WorkOrderDto?> GetWorkOrder(Guid id)
    {
        var workOrder = await _workOrderRepository.Get(id);
        return workOrder is null
            ? null
            : new WorkOrderDto(workOrder, await _shipmentRepository.GetForWorkOrder(id));
    }

    public async Task<WorkOrderHistoryDto?> GetWorkOrderHistory(Guid id)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(id);
        return workOrder is null ? null : new WorkOrderHistoryDto(workOrder);
    }

    /// <summary>
    /// The composed narrative (7.4), as opposed to <see cref="GetWorkOrderHistory"/>, which is
    /// the raw state log. Both stay: one is the audit trail, the other is the story.
    /// </summary>
    public async Task<WorkOrderTimelineDto?> GetWorkOrderTimeline(Guid id, CancellationToken cancellationToken = default)
    {
        var data = await _timelineRepository.GetTimelineData(id, cancellationToken);
        return data is null ? null : new WorkOrderTimelineDto(data);
    }

    public Task<WorkOrderCommandResponse> AdvanceWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id,
            wo => wo.AdvanceToNextStep(request.CreatedBy, request.Notes),
            onCommitted: PublishAdvanceEvents);

    public Task<WorkOrderCommandResponse> HoldWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.SetHold(request.CreatedBy, request.Notes));

    public Task<WorkOrderCommandResponse> ReleaseWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id,
            wo => wo.ReleaseHold(request.CreatedBy, request.Notes),
            onCommitted: wo => RerequestShippingIfStranded(wo, request.CreatedBy));

    public Task<WorkOrderCommandResponse> CancelWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id,
            wo => wo.Cancel(request.CreatedBy, request.Notes),
            onCommitted: VoidAnyBookedShipment);

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
            WorkOrder = new WorkOrderDto(workOrder, await _shipmentRepository.GetForWorkOrder(id))
        };
    }

    /// <summary>
    /// <strong>The one place a release re-triggers anything (7.3).</strong> An order released
    /// back into Delivery with no shipment is stranded: inspection has already passed it and
    /// nothing in the pipeline will move it again, so the API republishes
    /// <c>work-order.inspection-passed</c> and the existing shipping consumer books it. 7.1's
    /// unique index covers the case where a shipment appeared in the meantime.
    /// <para>
    /// Deliberately narrow. Releases that land in any other status stay inert, exactly as 5.3
    /// and 6.3 left them — this does not quietly re-arm the whole pipeline, and the general
    /// answer belongs to Epic 10's simulation engine, which can own a retry policy.
    /// </para>
    /// <para>
    /// It does mean the API publishes an event for a stage it doesn't own, which is worth stating
    /// plainly. The alternative — a <c>shipping-requested</c> key with the same consumer behind
    /// it — is more honest but is one more contract for one more caller, and the payload here is
    /// genuinely "these units passed inspection", which is what the key already means.
    /// </para>
    /// </summary>
    private async Task RerequestShippingIfStranded(WorkOrder workOrder, string releasedBy)
    {
        if (workOrder.CurrentStatus != WorkOrderStatus.Delivery)
        {
            return;
        }

        if (await _shipmentRepository.GetForWorkOrder(workOrder.Id) is not null)
        {
            return;
        }

        var serials = workOrder.AssignedStock
            .Where(unit => unit.Status == UnitStatus.Passed)
            .Select(unit => unit.SerialNumber)
            .ToList();

        if (serials.Count == 0)
        {
            _logger.LogWarning(
                "Work order {WorkOrderId} was released into Delivery with no passed units; not re-requesting shipping.",
                workOrder.Id);
            return;
        }

        // Recorded in state history as well as logged, so the recovery reads as something the
        // system did rather than as magic.
        workOrder.AppendNote(Author, $"Released into Delivery with no shipment; re-requesting a carrier for {serials.Count} unit(s).");
        await _workOrderRepository.Update(workOrder);

        await PublishSafely(new InspectionPassed(
            workOrder.Id,
            workOrder.OrderedItem.ItemId,
            serials,
            DateTime.UtcNow));

        _logger.LogInformation(
            "Work order {WorkOrderId} released by {ReleasedBy} into Delivery with no shipment; republished inspection-passed for {UnitCount} unit(s).",
            workOrder.Id, releasedBy, serials.Count);
    }

    /// <summary>
    /// A cancelled order must not leave a live parcel behind it (decided at 7.2). Only a booked,
    /// undispatched shipment can be voided — after dispatch the question cannot arise, because a
    /// Completed order refuses <c>Cancel</c> already.
    /// </summary>
    private async Task VoidAnyBookedShipment(WorkOrder workOrder)
    {
        var shipment = await _shipmentRepository.GetForWorkOrder(workOrder.Id);
        if (shipment is null)
        {
            return;
        }

        var voided = shipment.Void();
        if (!voided.Success)
        {
            _logger.LogWarning(
                "Work order {WorkOrderId} was cancelled but its shipment could not be voided: {Error}",
                workOrder.Id, voided.Error);
            return;
        }

        await _shipmentRepository.Update();

        _logger.LogInformation(
            "Work order {WorkOrderId} was cancelled; shipment {TrackingNumber} with {Carrier} voided.",
            workOrder.Id, shipment.TrackingNumber, shipment.Carrier);
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
