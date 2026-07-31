using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// Starts production once a work order's materials are reserved — the subscriber Epic 5 left
/// <c>work-order.materials-reserved</c> waiting for, which is why the pipeline stopped at
/// Scheduled until now.
/// <para>
/// Thin by design, like every handler here: adopt the inbound correlation id, call the service,
/// log the outcome. The attempt number comes off the payload. It used to be hard-coded to 1,
/// which was safe while materials could only be reserved once per order — 13.1 made a rebuild
/// draw its own parts, so this event now means "attempt N is supplied" and only the publisher
/// knows which N. It is still derived rather than read from the order: picking took it from the
/// event that triggered the pick, so a redelivery of either message computes the same attempt
/// and collides on the production run's unique key (6.4).
/// </para>
/// </summary>
public sealed class MaterialsReservedHandler : IIntegrationEventHandler<MaterialsReserved>
{
    private readonly ProductionService _production;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<MaterialsReservedHandler> _logger;

    public MaterialsReservedHandler(
        ProductionService production,
        CorrelationContext correlation,
        ILogger<MaterialsReservedHandler> logger)
    {
        _production = production;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<MaterialsReserved> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var result = await _production.Produce(
            envelope.Payload.WorkOrderId, envelope.Payload.AttemptNumber, cancellationToken);

        _logger.LogInformation(
            "Production for work order {WorkOrderId} attempt {Attempt} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.Payload.AttemptNumber,
            envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
