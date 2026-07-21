using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// Inspects what a production attempt just built. Thin adapter as always: the workflow — and
/// with it the decision to advance to Delivery, send the order back for rework, or fault it —
/// lives in <see cref="InspectionService"/>.
/// <para>
/// The attempt number comes off the wire rather than from the order, so a redelivery inspects
/// (and dedupes against) the same attempt the original message named.
/// </para>
/// </summary>
public sealed class ProductionCompletedHandler : IIntegrationEventHandler<ProductionCompleted>
{
    private readonly InspectionService _inspection;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<ProductionCompletedHandler> _logger;

    public ProductionCompletedHandler(
        InspectionService inspection,
        CorrelationContext correlation,
        ILogger<ProductionCompletedHandler> logger)
    {
        _inspection = inspection;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<ProductionCompleted> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var result = await _inspection.InspectAttempt(
            envelope.Payload.WorkOrderId, envelope.Payload.AttemptNumber, cancellationToken);

        _logger.LogInformation(
            "Inspection of work order {WorkOrderId} attempt {Attempt} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.Payload.AttemptNumber, envelope.EventType,
            envelope.EventId, result.Outcome, result.Summary);
    }
}
