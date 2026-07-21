using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// The factory's first real workflow stage: a scheduled work order has its materials picked.
/// <para>
/// Intentionally thin — the workflow itself lives in <see cref="MaterialPickingService"/> so it
/// can be reasoned about and tested without a broker. This adapter's only jobs are to carry the
/// inbound correlation id into anything the workflow publishes, and to translate the outcome
/// into an ack/nack decision.
/// </para>
/// <para>
/// <strong>Every outcome here acks.</strong> Insufficient stock (order → OnHold) and a duplicate
/// delivery are both <em>handled</em> results, not faults, so they return normally and the
/// consumer acks. Only an exception — a real transient fault — nacks. Keeping that line sharp is
/// what stops this epic from bleeding into Epic 8's retry/DLQ design.
/// </para>
/// </summary>
public sealed class WorkOrderScheduledHandler : IIntegrationEventHandler<WorkOrderScheduled>
{
    private readonly MaterialPickingService _picking;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<WorkOrderScheduledHandler> _logger;

    public WorkOrderScheduledHandler(
        MaterialPickingService picking,
        CorrelationContext correlation,
        ILogger<WorkOrderScheduledHandler> logger)
    {
        _picking = picking;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<WorkOrderScheduled> envelope, CancellationToken cancellationToken)
    {
        // Adopt the inbound correlation id for this message's scope so MaterialsReserved is
        // published under the same id the original API request started — one grep still spans
        // API → picking → production.
        _correlation.CorrelationId = envelope.CorrelationId;

        var result = await _picking.PickMaterials(envelope.Payload.WorkOrderId, cancellationToken);

        _logger.LogInformation(
            "Picking for work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
