using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// The way back round the loop: an order that came back from inspection short of its ordered
/// quantity re-enters the pipeline at <strong>picking</strong>, draws parts for the shortfall, and
/// reaches production the same way the original build did — through
/// <c>work-order.materials-reserved</c>.
/// <para>
/// This handler called <c>ProductionService.Produce</c> directly until 13.1, which made a rebuild
/// free: it built units out of nothing, because one reservation per order was the picking stage's
/// idempotency key and a second pick was impossible. Widening that key to
/// <c>(WorkOrderId, AttemptNumber)</c> is what let the loop become a genuine cycle — every stage
/// the first attempt went through, including the expensive one — and it means production keeps
/// exactly one entry point.
/// </para>
/// <para>
/// The rebuild is attempt <c>N + 1</c> and its demand is the event's <c>OutstandingQty</c>, both
/// <strong>taken from the payload</strong> rather than re-read from the order. That is deliberate:
/// a redelivery of this message must compute an identical attempt and an identical draw, or the
/// unique index it collides on would be guarding two different requests (6.4's rule, applied to
/// the pick).
/// </para>
/// </summary>
public sealed class ReworkRequiredHandler : IIntegrationEventHandler<ReworkRequired>
{
    private readonly MaterialPickingService _picking;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<ReworkRequiredHandler> _logger;

    public ReworkRequiredHandler(
        MaterialPickingService picking,
        CorrelationContext correlation,
        ILogger<ReworkRequiredHandler> logger)
    {
        _picking = picking;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<ReworkRequired> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var rebuildAttempt = envelope.Payload.AttemptNumber + 1;
        var result = await _picking.PickMaterials(
            envelope.Payload.WorkOrderId,
            rebuildAttempt,
            envelope.Payload.OutstandingQty,
            cancellationToken);

        _logger.LogInformation(
            "Rebuild attempt {Attempt} for work order {WorkOrderId} picking {OutstandingQty} unit(s) worth of parts "
            + "({ScrapCount} unit(s) scrapped on attempt {FailedAttempt}): {Outcome} — {Summary}",
            rebuildAttempt, envelope.Payload.WorkOrderId, envelope.Payload.OutstandingQty,
            envelope.Payload.Scrapped.Count, envelope.Payload.AttemptNumber, result.Outcome, result.Summary);
    }
}
