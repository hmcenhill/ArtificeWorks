using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// The pipeline's last stage: hand the booked parcel to the carrier and complete the work order.
/// <para>
/// Nothing here guards against redelivery, and nothing needs to — the shipment's own
/// <c>Booked → Dispatched</c> transition refuses a second hand-over, so unlike Epics 5 and 6 this
/// stage earns its idempotency from a state machine rather than from a unique index.
/// </para>
/// </summary>
public sealed class ShipmentScheduledHandler : IIntegrationEventHandler<ShipmentScheduled>
{
    private readonly ShippingService _shipping;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<ShipmentScheduledHandler> _logger;

    public ShipmentScheduledHandler(
        ShippingService shipping,
        CorrelationContext correlation,
        ILogger<ShipmentScheduledHandler> logger)
    {
        _shipping = shipping;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<ShipmentScheduled> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var result = await _shipping.DispatchShipment(envelope.Payload.WorkOrderId, cancellationToken);

        _logger.LogInformation(
            "Dispatch of work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
