using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.SubAssemblies;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// Closes 13.3's loop: a sub-assembly order has completed and its output is on the shelf, so the
/// parent that was waiting for it is released and re-scheduled to pick again.
/// <para>
/// <strong>This is the first pipeline subscriber <c>work-order.completed</c> has ever had.</strong>
/// It was left deliberately orphaned through Epics 7–8 as the terminal announcement, and since 11.2
/// it has had exactly one reader — the dashboard relay, which observes rather than acts. Binding it
/// here makes the parent's gate event-driven rather than a poll, and reuses the announcement the
/// whole system already understands instead of minting a <c>sub-assembly-stocked</c> key that would
/// say the same thing to one listener.
/// </para>
/// <para>
/// <strong>Nearly every delivery is a no-op</strong>, because nearly every order that completes is a
/// customer's. That costs one indexed read per completion, which is the price of not having a second
/// event type to keep in step with this one.
/// </para>
/// </summary>
public sealed class WorkOrderCompletedHandler : IIntegrationEventHandler<WorkOrderCompleted>
{
    private readonly SubAssemblyService _subAssemblies;
    private readonly CorrelationContext _correlation;
    private readonly ILogger<WorkOrderCompletedHandler> _logger;

    public WorkOrderCompletedHandler(
        SubAssemblyService subAssemblies,
        CorrelationContext correlation,
        ILogger<WorkOrderCompletedHandler> logger)
    {
        _subAssemblies = subAssemblies;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<WorkOrderCompleted> envelope, CancellationToken cancellationToken)
    {
        _correlation.CorrelationId = envelope.CorrelationId;

        var result = await _subAssemblies.ReleaseParentOfCompletedChild(
            envelope.Payload.WorkOrderId, cancellationToken);

        // Debug for the common no-op: a completion feed at Information, one line per finished order
        // saying "this had no parent", would drown the lines that matter.
        if (result.Outcome == ParentReleaseOutcome.NotASubAssembly)
        {
            _logger.LogDebug(
                "Work order {WorkOrderId} completed with no parent; nothing to release.",
                envelope.Payload.WorkOrderId);
            return;
        }

        _logger.LogInformation(
            "Parent release for work order {WorkOrderId} from {EventType} ({EventId}): {Outcome} — {Summary}",
            envelope.Payload.WorkOrderId, envelope.EventType, envelope.EventId, result.Outcome, result.Summary);
    }
}
