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

        // Both taken from the payload, never re-read off the order — the rule 6.4 set and 13.1
        // extended to picking. Since 13.3 this key has two publishers and they mean different
        // things by it: a new order asks for attempt 1 and everything it ordered; a parent released
        // by its finished sub-assembly asks for the attempt its short pick was buying for and only
        // what it still owes. The handler must not have to tell them apart.
        //
        // The floor is a compatibility repair, not a re-derivation: a `scheduled` message staged
        // before this field existed deserializes it as 0, which is not an attempt at all.
        var attemptNumber = envelope.Payload.AttemptNumber > 0
            ? envelope.Payload.AttemptNumber
            : MaterialPickingService.InitialAttempt;

        var result = await _picking.PickMaterials(
            envelope.Payload.WorkOrderId,
            attemptNumber,
            envelope.Payload.Quantity,
            cancellationToken);

        _logger.LogInformation(
            "Picking attempt {Attempt} for work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            attemptNumber, envelope.Payload.WorkOrderId, envelope.EventType, envelope.EventId,
            result.Outcome, result.Summary);
    }
}
