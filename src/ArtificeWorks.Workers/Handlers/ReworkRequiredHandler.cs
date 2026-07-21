using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// The other way into production: an order that came back from inspection short of its ordered
/// quantity. This handler is what closes the rework loop into a genuine cycle over the bus —
/// inspection publishes, the broker delivers, production builds again.
/// <para>
/// The rebuild is attempt <c>N + 1</c>, <strong>derived</strong> from the failed attempt the
/// event carries rather than read from the order's current state. That is deliberate: a
/// redelivery of this message computes the same number and collides on the production run's
/// unique key, where reading "what attempt are we on?" at handling time would be exactly the
/// check-then-act race the key exists to close (6.4).
/// </para>
/// </summary>
public sealed class ReworkRequiredHandler : IIntegrationEventHandler<ReworkRequired>
{
    private readonly ProductionService _production;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<ReworkRequiredHandler> _logger;

    public ReworkRequiredHandler(
        ProductionService production,
        CorrelationContext correlation,
        ILogger<ReworkRequiredHandler> logger)
    {
        _production = production;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<ReworkRequired> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var rebuildAttempt = envelope.Payload.AttemptNumber + 1;
        var result = await _production.Produce(envelope.Payload.WorkOrderId, rebuildAttempt, cancellationToken);

        _logger.LogInformation(
            "Rebuild attempt {Attempt} for work order {WorkOrderId} ({ScrapCount} unit(s) scrapped on attempt {FailedAttempt}): {Outcome} — {Summary}",
            rebuildAttempt, envelope.Payload.WorkOrderId, envelope.Payload.Scrapped.Count,
            envelope.Payload.AttemptNumber, result.Outcome, result.Summary);
    }
}
