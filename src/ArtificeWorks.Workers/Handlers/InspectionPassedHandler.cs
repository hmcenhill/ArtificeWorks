using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Application.SubAssemblies;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// Sends a passed work order to its ending — a carrier for a customer's order, the shelf for a
/// sub-assembly order. The subscriber Epic 6 left <c>work-order.inspection-passed</c> waiting for,
/// exactly as Epic 5 left <c>work-order.materials-reserved</c> waiting for Epic 6.
/// <para>
/// <strong>This is the one branch 13.3 adds to the pipeline.</strong> An order <em>with a parent</em>
/// is stocked rather than shipped: its passed units credit the component it was making, and it
/// completes with no carrier and no parcel. Putaway is asked first and answers
/// <see cref="PutawayOutcome.NotASubAssembly"/> for everything else, so the shipping path below is
/// byte-for-byte what Epic 7 shipped — the ordering is deliberate, because "is this internal?" is
/// cheaper to answer than "can a carrier take it?" and only one of the two can be true.
/// </para>
/// <para>
/// Thin by design, like every handler here. The passing serials come off the wire rather than
/// being re-derived from the order, so a redelivery would describe the same parcel — though the
/// unique index on <c>shipments.work_order_id</c> (or, for putaway, the child's own terminal
/// transition) is what actually stops a second one.
/// </para>
/// <para>
/// Since 7.3 this key has a <strong>second publisher</strong>: the API republishes it when a
/// visitor releases an order held at Delivery with no shipment. Nothing here needs to know which
/// publisher it came from, which is the point.
/// </para>
/// </summary>
public sealed class InspectionPassedHandler : IIntegrationEventHandler<InspectionPassed>
{
    private readonly ShippingService _shipping;
    private readonly StockPutawayService _putaway;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<InspectionPassedHandler> _logger;

    public InspectionPassedHandler(
        ShippingService shipping,
        StockPutawayService putaway,
        CorrelationContext correlation,
        ILogger<InspectionPassedHandler> logger)
    {
        _shipping = shipping;
        _putaway = putaway;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<InspectionPassed> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var workOrderId = envelope.Payload.WorkOrderId;

        var putaway = await _putaway.TryPutAway(workOrderId, envelope.Payload.SerialNumbers, cancellationToken);
        if (putaway.Outcome != PutawayOutcome.NotASubAssembly)
        {
            _logger.LogInformation(
                "Putaway for sub-assembly work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
                workOrderId, envelope.EventType, envelope.EventId, putaway.Outcome, putaway.Summary);
            return;
        }

        var result = await _shipping.BookForPassedInspection(
            workOrderId, envelope.Payload.SerialNumbers, cancellationToken);

        _logger.LogInformation(
            "Shipping for work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            workOrderId, envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
