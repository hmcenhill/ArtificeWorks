using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.SubAssemblies;

/// <summary>How putting a finished sub-assembly order away ended.</summary>
public enum PutawayOutcome
{
    /// <summary>The order has no parent — a customer's order, which ships. Not this service's business.</summary>
    NotASubAssembly,

    /// <summary>Stock credited, the order Completed, the completion announced.</summary>
    StockedAway,

    /// <summary>Already Completed — a redelivery. Nothing written.</summary>
    AlreadyStocked,

    /// <summary>The order is not in Delivery (held, cancelled, still building), so there is nothing to put away.</summary>
    NotInDelivery,

    /// <summary>Nothing passed inspection, so there is no stock to credit.</summary>
    NothingToStock,

    /// <summary>The order names a component the catalog doesn't have — the units exist, the shelf doesn't.</summary>
    ComponentNotFound,

    /// <summary>No such work order.</summary>
    WorkOrderNotFound
}

public sealed record PutawayResult(PutawayOutcome Outcome, string Summary, uint QuantityStocked = 0);

/// <summary>
/// The one branch the pipeline gains in 13.3: at <c>work-order.inspection-passed</c>, an order
/// <em>with a parent</em> goes to the shelf instead of to a carrier.
/// <para>
/// <strong>Stocked, not shipped.</strong> Booking a fictional carrier to move parts from one end of
/// the factory to the other would be a lie told to avoid one <c>if</c>. So there is no shipment row,
/// no carrier and no tracking number — the passed quantity is credited to
/// <c>components.on_hand</c> for the component the child was making, and the parent's re-pick draws
/// it back off the shelf through the ordinary atomic decrement 5.3 wrote.
/// </para>
/// <para>
/// <strong>Stock, not allocation.</strong> A made component is fungible: the units this child built
/// are not earmarked for its parent, and another order may take them first. That is a real cost,
/// stated plainly — you cannot trace which control stack went into which Courier — and the parent's
/// re-pick is what makes it safe: a pick that is still short simply holds again and asks again.
/// </para>
/// <para>
/// <strong>The terminal transition is the dedupe key.</strong> No new table: the child's Delivery →
/// Completed transition commits in the same transaction as the credit, so a redelivered
/// <c>inspection-passed</c> finds a Completed order and writes nothing. A genuine race is settled by
/// the work order's <c>xmin</c> token — the loser's save throws, its credit rolls back with it, and
/// its redelivery takes the duplicate path.
/// </para>
/// </summary>
public sealed class StockPutawayService
{
    /// <summary>Author recorded against state-history entries this workflow writes.</summary>
    public const string Author = "putaway-worker";

    private readonly IWorkOrderRepository _workOrders;
    private readonly IComponentStockRepository _stock;
    private readonly IEventPublisher _eventPublisher;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<StockPutawayService> _logger;

    public StockPutawayService(
        IWorkOrderRepository workOrders,
        IComponentStockRepository stock,
        IEventPublisher eventPublisher,
        ArtificeWorksMetrics metrics,
        ILogger<StockPutawayService> logger)
    {
        _workOrders = workOrders;
        _stock = stock;
        _eventPublisher = eventPublisher;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Puts a passed sub-assembly order away, or reports that this order is not one — in which case
    /// the caller falls through to shipping. The branch is here rather than in
    /// <c>ShippingService</c> so that "an internal order has no carrier" is a statement the code
    /// makes, not one that shipping has to keep making room for.
    /// </summary>
    /// <param name="serialNumbers">
    /// The passing serials as the event described them, trusted over re-deriving from the order —
    /// the same reading shipping takes, so a redelivery describes the same units. Empty falls back
    /// to the order's own passed quantity.
    /// </param>
    public async Task<PutawayResult> TryPutAway(
        Guid workOrderId,
        IReadOnlyList<Guid> serialNumbers,
        CancellationToken cancellationToken = default)
    {
        ArtificeWorksTelemetry.StampWorkOrder(workOrderId);

        var workOrder = await _workOrders.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Putaway requested for unknown work order {WorkOrderId}.", workOrderId);
            return new PutawayResult(PutawayOutcome.WorkOrderNotFound, $"No work order found with id {workOrderId}.");
        }

        if (workOrder.ForComponentId is not { } componentId)
        {
            // The overwhelmingly common case: an ordinary order, which ships.
            return new PutawayResult(PutawayOutcome.NotASubAssembly,
                $"Work order {workOrderId} is not a sub-assembly order; it ships.");
        }

        if (workOrder.CurrentStatus == WorkOrderStatus.Completed)
        {
            // A redelivery. Nothing is written — deliberately not even a state-history note, since a
            // note per redelivery would itself be a non-idempotent side effect — but it IS logged.
            var summary = $"Work order {workOrderId} was already stocked away; skipping duplicate.";
            _logger.LogInformation("Duplicate putaway skipped (idempotent): {Summary}", summary);
            return new PutawayResult(PutawayOutcome.AlreadyStocked, summary);
        }

        if (workOrder.CurrentStatus != WorkOrderStatus.Delivery)
        {
            var summary =
                $"Work order is {workOrder.CurrentStatus}, not Delivery; nothing is ready to put away.";
            _logger.LogInformation("Putaway rejected for work order {WorkOrderId}: {Summary}", workOrderId, summary);
            return new PutawayResult(PutawayOutcome.NotInDelivery, summary);
        }

        var quantity = serialNumbers.Count > 0 ? (uint)serialNumbers.Count : workOrder.PassedQty;
        if (quantity == 0)
        {
            var summary = $"Work order {workOrderId} has no passed units to put away.";
            _logger.LogWarning("Putaway rejected: {Summary}", summary);
            return new PutawayResult(PutawayOutcome.NothingToStock, summary);
        }

        var summaryText = $"Stocked {quantity} × {componentId}.";
        var from = workOrder.CurrentStatus;

        // Delivery → Completed. The child's pipeline ends here: no carrier, no parcel, no tracking
        // number, just a shelf that is fuller than it was.
        var advance = workOrder.AdvanceToNextStep(Author, ProductionService.Truncate(summaryText));
        if (!advance.Success)
        {
            _logger.LogInformation(
                "Sub-assembly work order {WorkOrderId} could not be completed ({Code}): {Error}",
                workOrderId, advance.Code, advance.Error);
            return new PutawayResult(PutawayOutcome.NotInDelivery, advance.Error!);
        }

        // The completion is announced with the key the whole system already understands, so the
        // relay, the feed and the board treat a stocked child exactly as they treat a shipped order
        // — and so 13.3's own release subscriber has something to listen to.
        var credited = await _stock.TryCredit(componentId, quantity, stageWithCredit: () =>
            _eventPublisher.PublishAsync(new WorkOrderCompleted(
                workOrder.Id,
                workOrder.OrderedItem.ItemId,
                Carrier: null,
                TrackingNumber: null,
                serialNumbers.Count > 0 ? serialNumbers : PassedSerials(workOrder),
                DateTime.UtcNow,
                ForComponentId: componentId), cancellationToken),
            cancellationToken);

        if (!credited)
        {
            // The transaction rolled back, so the order is still in Delivery and the redelivery will
            // try again. Error, not Warning: a sub-assembly whose component has vanished from the
            // catalog is a broken world, not a busy one.
            var summary =
                $"Work order {workOrderId} makes component {componentId}, which is not in the catalog; "
                + "nothing was stocked.";
            _logger.LogError("Putaway failed: {Summary}", summary);
            return new PutawayResult(PutawayOutcome.ComponentNotFound, summary);
        }

        _metrics.Transition(from.ToString(), workOrder.CurrentStatus.ToString(), workOrder.Origin.ToString());

        _logger.LogInformation(
            "Sub-assembly work order {WorkOrderId} completed: {Quantity} × {ComponentId} put away for "
            + "parent {ParentId}.",
            workOrderId, quantity, componentId, workOrder.ParentWorkOrderId);

        return new PutawayResult(PutawayOutcome.StockedAway, summaryText, quantity);
    }

    private static List<Guid> PassedSerials(WorkOrder workOrder) =>
        workOrder.AssignedStock
            .Where(unit => unit.Status == Domain.Models.Materials.UnitStatus.Passed)
            .Select(unit => unit.SerialNumber)
            .ToList();
}
