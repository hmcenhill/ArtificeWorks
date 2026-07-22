using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
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
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<WorkOrderHandler> _logger;

    public WorkOrderHandler(IWorkOrderRepository workOrderRepository,
        IProductRepository productRepository,
        IShipmentRepository shipmentRepository,
        IWorkOrderTimelineRepository timelineRepository,
        IEventPublisher eventPublisher,
        ArtificeWorksMetrics metrics,
        ILogger<WorkOrderHandler> logger)
    {
        _metrics = metrics;
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
            beforeCommit: PublishAdvanceEvents);

    public Task<WorkOrderCommandResponse> HoldWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id, wo => wo.SetHold(request.CreatedBy, request.Notes));

    public Task<WorkOrderCommandResponse> ReleaseWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id,
            wo => wo.ReleaseHold(request.CreatedBy, request.Notes),
            beforeCommit: wo => RerequestShippingIfStranded(wo, request.CreatedBy));

    public Task<WorkOrderCommandResponse> CancelWorkOrder(Guid id, WorkOrderCommandRequest request)
        => ExecuteCommand(id,
            wo => wo.Cancel(request.CreatedBy, request.Notes),
            beforeCommit: VoidAnyBookedShipment);

    /// <param name="beforeCommit">
    /// Ran between the domain transition and the single <c>SaveChanges</c> that persists it —
    /// which since 8.1 is <em>where publishing happens</em>. The hook used to be
    /// <c>onCommitted</c> and published to the broker after the state change had already landed;
    /// that ordering was the dual-write gap. Now the event is staged as an outbox row and the
    /// transition and its announcement commit in one transaction, so neither can exist without
    /// the other.
    /// </param>
    private async Task<WorkOrderCommandResponse> ExecuteCommand(Guid id,
        Func<WorkOrder, TransitionResult> command,
        Func<WorkOrder, Task>? beforeCommit = null)
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

        ArtificeWorksTelemetry.StampWorkOrder(id);

        var from = workOrder.CurrentStatus;
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

        // Stage-then-commit: any event this transition raises is written to the outbox in the
        // same unit of work as the transition itself, and one SaveChanges commits both.
        if (beforeCommit is not null)
        {
            await beforeCommit(workOrder);
        }

        await _workOrderRepository.Update(workOrder);

        // Every API-driven transition, counted once, here — the single funnel all four commands
        // (advance/hold/release/cancel) already go through. The workflow services count the ones
        // events drive; between them a stage change is counted in exactly one place.
        _metrics.Transition(from.ToString(), workOrder.CurrentStatus.ToString());

        _logger.LogInformation(
            "Work order {WorkOrderId} moved {From} → {To}.",
            workOrder.Id, from, workOrder.CurrentStatus);

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
        // system did rather than as magic. Note and event are both left tracked for the caller's
        // single SaveChanges — the release, the note and the re-request are one commit.
        workOrder.AppendNote(Author, $"Released into Delivery with no shipment; re-requesting a carrier for {serials.Count} unit(s).");

        await _eventPublisher.PublishAsync(new InspectionPassed(
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

        // No save here since 8.1: the shipment is tracked by the same scoped context as the work
        // order, so the caller's single SaveChanges voids the parcel and cancels the order together.
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
            return _eventPublisher.PublishAsync(new WorkOrderScheduled(
                workOrder.Id,
                workOrder.OrderedItem.ItemId,
                workOrder.OrderedItem.ItemName,
                workOrder.OrderItemQty,
                workOrder.UpdatedUtc));
        }
        return Task.CompletedTask;
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

        // The aggregate makes its own id in the constructor, so the announcement can be staged
        // before the order is saved — which is the point. The outbox row, the work order, and
        // (when the caller supplied one) 8.4's idempotency key all land in the same
        // SaveChanges below: one transaction containing the work, its announcement, and the
        // marker that says it happened. That is the whole epic in one commit.
        await _eventPublisher.PublishAsync(new WorkOrderCreated(
            newOrder.Id,
            newOrder.OrderedItem.ItemId,
            newOrder.OrderedItem.ItemName,
            newOrder.OrderItemQty,
            request.Requestor,
            newOrder.CreatedUtc));

        try
        {
            var savedWorkOrder = await _workOrderRepository.Add(newOrder);
            if (savedWorkOrder is not null)
            {
                _metrics.WorkOrderCreated();
                ArtificeWorksTelemetry.StampWorkOrder(newOrder.Id);

                _logger.LogInformation(
                    "Work order {WorkOrderId} created for {Qty} × {ProductId} by {Requestor}.",
                    newOrder.Id, newOrder.OrderItemQty, product.ItemId, request.Requestor);

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
        catch (DuplicateKeyException)
        {
            // Not this handler's problem to answer, and it must escape the catch-all below. A
            // unique-key collision on the way out of create means two requests carried the same
            // Idempotency-Key (8.4); the filter that owns that contract is the only thing that
            // can replay the winner's response. Swallowing it would turn a resolvable race into
            // a 500.
            throw;
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
