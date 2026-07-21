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
/// log the outcome. The attempt number is <strong>1</strong>, hard-coded rather than inferred —
/// materials are reserved exactly once per order (Epic 5's unique index guarantees it), so this
/// event can only ever mean the initial build. A rebuild arrives as
/// <see cref="ReworkRequiredHandler"/> instead.
/// </para>
/// </summary>
public sealed class MaterialsReservedHandler : IIntegrationEventHandler<MaterialsReserved>
{
    private const int InitialAttempt = 1;

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

        var result = await _production.Produce(envelope.Payload.WorkOrderId, InitialAttempt, cancellationToken);

        _logger.LogInformation(
            "Production for work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
