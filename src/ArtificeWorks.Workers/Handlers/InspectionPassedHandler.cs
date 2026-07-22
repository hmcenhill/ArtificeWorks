using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// Books a carrier once a work order's full quantity has passed — the subscriber Epic 6 left
/// <c>work-order.inspection-passed</c> waiting for, exactly as Epic 5 left
/// <c>work-order.materials-reserved</c> waiting for Epic 6.
/// <para>
/// Thin by design, like every handler here. The passing serials come off the wire rather than
/// being re-derived from the order, so a redelivery would describe the same parcel — though the
/// unique index on <c>shipments.work_order_id</c> is what actually stops it booking a second one.
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
    private readonly CorrelationContext _correlation;
    private readonly ILogger<InspectionPassedHandler> _logger;

    public InspectionPassedHandler(
        ShippingService shipping,
        CorrelationContext correlation,
        ILogger<InspectionPassedHandler> logger)
    {
        _shipping = shipping;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<InspectionPassed> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var result = await _shipping.BookForPassedInspection(
            envelope.Payload.WorkOrderId, envelope.Payload.SerialNumbers, cancellationToken);

        _logger.LogInformation(
            "Shipping for work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
