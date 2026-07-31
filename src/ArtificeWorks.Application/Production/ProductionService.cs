using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using System.Diagnostics;

using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Production;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Production;

/// <summary>
/// The production workflow: given a work order whose materials are reserved (or one sent back
/// for rework), build the outstanding quantity as serialized units, start production, and hand
/// the pipeline on to inspection.
/// <para>
/// <strong>One entry point, one trigger</strong> (since 13.1). Every attempt arrives as
/// <c>work-order.materials-reserved</c>, carrying its own attempt number: the initial pick
/// announces attempt 1, and a rebuild's pick announces attempt N+1. <c>work-order.rework-required</c>
/// no longer reaches this service at all — it re-enters the pipeline at picking, so a rebuild goes
/// through every stage the original build did. That is what makes the rework loop a real cycle
/// rather than a shortcut past the expensive stage.
/// </para>
/// <para>
/// <strong>Production is instant.</strong> No timers, no delay, no <c>due at</c> sweeper; Epic 10's
/// simulation engine owns pacing. A sleeping handler here would also block the prefetch-1
/// consumer for the whole build and buy nothing.
/// </para>
/// <para>
/// <strong>Rebuilds consume real materials</strong> (13.1, paying off the simplification 6.3 took
/// on). A scrapped unit burns its parts, and the units this service builds on attempt N were
/// supplied by a pick for attempt N — so there is nothing here that needs to know about materials,
/// only that it does not get asked to build until they exist.
/// </para>
/// <para>
/// <strong>Every outcome acks.</strong> A duplicate attempt, a held order, an out-of-sequence
/// attempt — all handled results. Only an exception nacks.
/// </para>
/// </summary>
public sealed class ProductionService
{
    /// <summary>Author recorded against state-history entries this workflow writes.</summary>
    public const string Author = "production-worker";

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IProductionRunRepository _runRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<ProductionService> _logger;

    public ProductionService(
        IWorkOrderRepository workOrderRepository,
        IProductionRunRepository runRepository,
        IEventPublisher eventPublisher,
        ArtificeWorksMetrics metrics,
        ILogger<ProductionService> logger)
    {
        _workOrderRepository = workOrderRepository;
        _runRepository = runRepository;
        _eventPublisher = eventPublisher;
        _metrics = metrics;
        _logger = logger;
    }

    /// <param name="attemptNumber">Which attempt to build, read off <c>MaterialsReserved</c>. The
    /// publisher derived it from the event that triggered the pick, so a redelivery of either
    /// message computes the same number and collides on the run's unique key instead of building a
    /// second batch.</param>
    public async Task<ProductionResult> Produce(
        Guid workOrderId,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        ArtificeWorksTelemetry.StampWorkOrder(workOrderId);
        Activity.Current?.SetTag(ArtificeWorksTelemetry.AttemptAttribute, attemptNumber);

        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Production requested for unknown work order {WorkOrderId}; nothing to build.", workOrderId);
            return new ProductionResult(ProductionOutcome.WorkOrderNotFound,
                $"No work order found with id {workOrderId}.", attemptNumber);
        }

        // Cheap pre-check for the common duplicate case. Not the guarantee — two deliveries can
        // pass it together — the unique index on (WorkOrderId, AttemptNumber) is. See below.
        if (await _runRepository.AttemptExists(workOrderId, attemptNumber, cancellationToken))
        {
            return AlreadyBuilt(workOrderId, attemptNumber);
        }

        var outstanding = workOrder.OutstandingQty;
        var from = workOrder.CurrentStatus;
        var build = workOrder.Build(Author, attemptNumber,
            Truncate(attemptNumber == 1
                ? $"Production started: building {outstanding} unit(s)."
                : $"Rebuild attempt {attemptNumber}: building {outstanding} outstanding unit(s)."));

        if (!build.Success)
        {
            _logger.LogInformation(
                "Work order {WorkOrderId} could not start attempt {Attempt} ({Code}): {Error}",
                workOrderId, attemptNumber, build.Code, build.Error);
            return new ProductionResult(ProductionOutcome.Rejected, build.Error!, attemptNumber);
        }

        var built = workOrder.AssignedStock
            .Where(unit => unit.BuildAttempt == attemptNumber)
            .Select(unit => unit.SerialNumber)
            .ToList();

        // Staged before the commit, not published after it (8.1): the hand-off to inspection is
        // an outbox row on the same unit of work as the build.
        await _eventPublisher.PublishAsync(new ProductionCompleted(
            workOrder.Id,
            workOrder.OrderedItem.ItemId,
            built,
            attemptNumber,
            DateTime.UtcNow), cancellationToken);

        // The run row, the new units, the Scheduled → InProcess transition, its history entry and
        // now the outbox row all commit in one SaveChanges. A concurrent duplicate loses on the
        // unique key and its units roll back with it — along with its announcement, so the loser
        // is silent as well as inert. An attempt builds exactly once, and is announced exactly
        // once (6.4, extended by 8.1).
        var committed = await _runRepository.TryCommitAttempt(
            new ProductionRun(workOrderId, attemptNumber, (uint)built.Count), cancellationToken);

        if (!committed)
        {
            return AlreadyBuilt(workOrderId, attemptNumber);
        }

        // Counted after the commit succeeded, not after the domain call: a losing duplicate rolls
        // its units back, and a counter that moved anyway is exactly the double-count 9.2's tests
        // look for.
        _metrics.Transition(from.ToString(), workOrder.CurrentStatus.ToString(), workOrder.Origin.ToString());
        _metrics.UnitsBuilt(built.Count, attemptNumber);

        var summary = $"Built {built.Count} unit(s) on attempt {attemptNumber}.";
        _logger.LogInformation(
            "Work order {WorkOrderId} attempt {Attempt}: built {UnitCount} unit(s); order is {Status}.",
            workOrderId, attemptNumber, built.Count, workOrder.CurrentStatus);

        return new ProductionResult(ProductionOutcome.Built, summary, attemptNumber, built);
    }

    /// <summary>
    /// A redelivery. Nothing is written — deliberately not even a state-history note, since a
    /// note per redelivery would itself be a non-idempotent side effect — but it IS logged, so
    /// idempotency stays observable when Epic 12 redelivers a message in front of an audience.
    /// </summary>
    private ProductionResult AlreadyBuilt(Guid workOrderId, int attemptNumber)
    {
        var summary = $"Work order {workOrderId} has already built attempt {attemptNumber}; skipping duplicate.";
        _logger.LogInformation("Duplicate production skipped (idempotent): {Summary}", summary);
        return new ProductionResult(ProductionOutcome.AlreadyBuilt, summary, attemptNumber);
    }

    // State-history notes are capped at 500 chars by the schema.
    internal static string Truncate(string note) => note.Length <= 500 ? note : note[..497] + "...";
}
