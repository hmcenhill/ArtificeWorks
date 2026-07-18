using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Workers.Consuming;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Handlers;

/// <summary>
/// First end-to-end proof of the async backbone: on <see cref="WorkOrderScheduled"/> the
/// worker loads the order and appends a state-history note, so a consumed event leaves an
/// observable trace in the database. Later workflow epics replace this stand-in with real
/// material-picking behaviour.
/// </summary>
public sealed class WorkOrderScheduledHandler : IIntegrationEventHandler<WorkOrderScheduled>
{
    private const string Author = "worker";

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly ILogger<WorkOrderScheduledHandler> _logger;

    public WorkOrderScheduledHandler(
        IWorkOrderRepository workOrderRepository,
        ILogger<WorkOrderScheduledHandler> logger)
    {
        _workOrderRepository = workOrderRepository;
        _logger = logger;
    }

    public async Task HandleAsync(EventEnvelope<WorkOrderScheduled> envelope, CancellationToken cancellationToken)
    {
        var scheduled = envelope.Payload;

        var workOrder = await _workOrderRepository.GetWithHistory(scheduled.WorkOrderId);
        if (workOrder is null)
        {
            // The order was published but isn't found — log and move on (the message is
            // still acked; there is nothing to retry against).
            _logger.LogWarning(
                "Received {EventType} for unknown work order {WorkOrderId}; nothing to touch.",
                envelope.EventType, scheduled.WorkOrderId);
            return;
        }

        workOrder.AppendNote(Author, $"Scheduling acknowledged by worker (event {envelope.EventId}).");
        await _workOrderRepository.Update(workOrder);

        _logger.LogInformation(
            "Handled {EventType} for work order {WorkOrderId} [correlation {CorrelationId}]; appended state-history note.",
            envelope.EventType, scheduled.WorkOrderId, envelope.CorrelationId);
    }
}
