using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Production;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Inspection;

/// <summary>
/// The inspection workflow: judge the units a production attempt built, one at a time, then
/// decide what the order does next.
/// <para>
/// <strong>Verdicts are per unit; the order-level outcome is derived.</strong> Nothing anywhere
/// says "the order passed" — that is read off by counting passing units against the ordered
/// quantity, which is what makes serialization worth having and what lets a rebuild top up a
/// partial success instead of starting over.
/// </para>
/// <para>
/// <strong>Two ways in, one resolution.</strong> The consumer inspects a whole attempt with
/// verdicts from <see cref="IVerdictSource"/>; the API records one verdict by hand. Both apply
/// the same domain call and both end in <see cref="Resolve"/>, so a factory run by a visitor
/// and one run unattended reach identical state.
/// </para>
/// </summary>
public sealed class InspectionService
{
    /// <summary>Author recorded against state-history entries the automatic path writes.</summary>
    public const string Author = "inspection-worker";

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IInspectionRunRepository _runRepository;
    private readonly IVerdictSource _verdicts;
    private readonly IEventPublisher _eventPublisher;
    private readonly InspectionConfiguration _inspectionConfig;
    private readonly ProductionConfiguration _productionConfig;
    private readonly ILogger<InspectionService> _logger;

    public InspectionService(
        IWorkOrderRepository workOrderRepository,
        IInspectionRunRepository runRepository,
        IVerdictSource verdicts,
        IEventPublisher eventPublisher,
        InspectionConfiguration inspectionConfig,
        ProductionConfiguration productionConfig,
        ILogger<InspectionService> logger)
    {
        _workOrderRepository = workOrderRepository;
        _runRepository = runRepository;
        _verdicts = verdicts;
        _eventPublisher = eventPublisher;
        _inspectionConfig = inspectionConfig;
        _productionConfig = productionConfig;
        _logger = logger;
    }

    // ------------------------------------------------------------------ the consumer path

    /// <summary>
    /// Inspects everything build <paramref name="attemptNumber"/> produced: advance the order
    /// into Inspection, verdict each unit still awaiting one, and resolve the order's outcome.
    /// </summary>
    public async Task<InspectionResult> InspectAttempt(
        Guid workOrderId,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Inspection requested for unknown work order {WorkOrderId}.", workOrderId);
            return new InspectionResult(InspectionOutcome.WorkOrderNotFound,
                $"No work order found with id {workOrderId}.", attemptNumber);
        }

        // Cheap pre-check; the unique index on (WorkOrderId, AttemptNumber) is the guarantee.
        if (await _runRepository.AttemptExists(workOrderId, attemptNumber, cancellationToken))
        {
            return AlreadyInspected(workOrderId, attemptNumber);
        }

        if (workOrder.CurrentStatus == WorkOrderStatus.InProcess)
        {
            var advance = workOrder.AdvanceToNextStep(Author,
                $"Attempt {attemptNumber} entered inspection.");
            if (!advance.Success)
            {
                return Rejected(workOrder, attemptNumber, advance.Error!);
            }
        }
        else if (workOrder.CurrentStatus != WorkOrderStatus.Inspection)
        {
            return Rejected(workOrder, attemptNumber,
                $"Work order is {workOrder.CurrentStatus} and cannot be inspected.");
        }

        var run = new InspectionRun(workOrderId, attemptNumber);
        uint passed = 0, scrapped = 0;

        if (_inspectionConfig.AutoInspect)
        {
            foreach (var unit in workOrder.UnitsAwaitingInspection(attemptNumber))
            {
                var verdict = _verdicts.Verdict(unit);
                var applied = verdict.Passed
                    ? unit.Pass()
                    : unit.Scrap(verdict.Reason ?? _inspectionConfig.AutoFailureReason);

                if (!applied.Success)
                {
                    // Only reachable if a human verdicted this unit between the load and now.
                    // Their verdict stands; ours is dropped. That is the intended resolution.
                    _logger.LogInformation(
                        "Unit {Serial} on work order {WorkOrderId} was verdicted elsewhere first: {Error}",
                        unit.SerialNumber, workOrderId, applied.Error);
                    continue;
                }

                if (verdict.Passed) { passed++; } else { scrapped++; }
            }

            workOrder.AppendNote(Author, ProductionService.Truncate(
                $"Inspected attempt {attemptNumber}: {passed} passed, {scrapped} scrapped."));
        }
        else
        {
            _logger.LogInformation(
                "Auto-inspection is off; attempt {Attempt} of work order {WorkOrderId} awaits manual verdicts.",
                attemptNumber, workOrderId);
        }

        run.RecordVerdicts(passed, scrapped);
        var resolution = Resolve(workOrder, Author);

        // Staged before the commit (8.1), so the outcome's announcement rides the same
        // transaction as the verdicts that produced it.
        await PublishResolution(workOrder, resolution, cancellationToken);

        // Verdicts, the run row (the dedupe key), whatever transition the verdicts caused and the
        // outbox row announcing it all commit together. A concurrent duplicate loses on the
        // unique key and takes its verdicts, its transition and its announcement down with it —
        // so no second InspectionPassed and no double-burned rebuild attempt (6.4).
        if (!await _runRepository.TryCommitInspection(run, cancellationToken))
        {
            return AlreadyInspected(workOrderId, attemptNumber);
        }

        _logger.LogInformation(
            "Work order {WorkOrderId} attempt {Attempt}: {Passed} passed, {Scrapped} scrapped — {Outcome}.",
            workOrderId, attemptNumber, passed, scrapped, resolution.Outcome);

        return new InspectionResult(resolution.Outcome, resolution.Summary, attemptNumber, passed, scrapped);
    }

    // ---------------------------------------------------------------------- the API path

    /// <summary>
    /// Records one verdict by hand — the visitor's decision moment, and the same path Epic 12's
    /// failure injection uses rather than a back door of its own. The order does not need to be
    /// idle: if the auto-inspector reached this unit first, the double-verdict guard resolves it
    /// as a clean conflict.
    /// </summary>
    public async Task<VerdictResult> RecordVerdict(
        Guid workOrderId,
        Guid serialNumber,
        bool passed,
        string? reason,
        string recordedBy,
        CancellationToken cancellationToken = default)
    {
        if (!passed && string.IsNullOrWhiteSpace(reason))
        {
            return new VerdictResult(VerdictOutcome.ReasonRequired,
                "A failing verdict must carry a reason.");
        }

        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            return new VerdictResult(VerdictOutcome.WorkOrderNotFound,
                $"No work order found with id {workOrderId}.");
        }

        var unit = workOrder.FindUnit(serialNumber);
        if (unit is null)
        {
            return new VerdictResult(VerdictOutcome.UnitNotFound,
                $"Work order {workOrderId} has no unit with serial number {serialNumber}.");
        }

        if (workOrder.CurrentStatus != WorkOrderStatus.Inspection)
        {
            return new VerdictResult(VerdictOutcome.NotInInspection,
                $"Work order is {workOrder.CurrentStatus}; its units are not up for inspection.");
        }

        var applied = passed ? unit.Pass() : unit.Scrap(reason!);
        if (!applied.Success)
        {
            return new VerdictResult(VerdictOutcome.AlreadyInspected, applied.Error!);
        }

        var summary = passed
            ? $"Unit {serialNumber} passed inspection."
            : $"Unit {serialNumber} scrapped: {reason}";
        workOrder.AppendNote(recordedBy, ProductionService.Truncate(summary));

        // A manual verdict can be the one that completes the attempt, so it resolves the
        // order-level outcome exactly as the automatic path does. No run row is written: this
        // is a single-unit decision, and the unit's own verdict guard is its dedupe.
        var resolution = Resolve(workOrder, recordedBy);
        await PublishResolution(workOrder, resolution, cancellationToken);
        await _workOrderRepository.Update(workOrder);

        _logger.LogInformation(
            "Manual verdict by {RecordedBy} on unit {Serial} of work order {WorkOrderId}: {Summary} — {Outcome}.",
            recordedBy, serialNumber, workOrderId, summary, resolution.Outcome);

        return new VerdictResult(VerdictOutcome.Recorded, summary, resolution.Outcome);
    }

    // ------------------------------------------------------------------- shared resolution

    private sealed record Resolution(InspectionOutcome Outcome, string Summary);

    /// <summary>
    /// Decides what the order does now that some verdicts are in, and applies it. Three ways
    /// out and one way to wait:
    /// <list type="bullet">
    ///   <item>full quantity passed → Delivery</item>
    ///   <item>units still awaiting a verdict → nothing yet</item>
    ///   <item>short, with rebuilds left → back to InProcess for another attempt</item>
    ///   <item>short, cap exhausted → Fault, and the cycle stops</item>
    /// </list>
    /// </summary>
    private Resolution Resolve(WorkOrder workOrder, string author)
    {
        if (workOrder.IsFulfilled)
        {
            var advance = workOrder.AdvanceToNextStep(author,
                $"All {workOrder.OrderItemQty} unit(s) passed inspection.");

            return advance.Success
                ? new Resolution(InspectionOutcome.Passed,
                    $"Full ordered quantity passed; work order advanced to {workOrder.CurrentStatus}.")
                : new Resolution(InspectionOutcome.Rejected, advance.Error!);
        }

        if (workOrder.AssignedStock.Any(unit => unit.Status == UnitStatus.Built))
        {
            var pending = workOrder.AssignedStock.Count(unit => unit.Status == UnitStatus.Built);
            return new Resolution(InspectionOutcome.AwaitingVerdicts,
                $"{pending} unit(s) still awaiting a verdict.");
        }

        // Short, and every unit has been judged. The rebuild that would follow attempt N is
        // rebuild number N, so the cap is exceeded exactly when the attempt count passes it.
        if (workOrder.BuildAttempt > _productionConfig.MaxRebuildAttempts)
        {
            var reason = ProductionService.Truncate(
                $"Rebuild cap of {_productionConfig.MaxRebuildAttempts} exceeded after {workOrder.BuildAttempt} " +
                $"attempt(s); {workOrder.PassedQty} of {workOrder.OrderItemQty} unit(s) passed. " +
                $"Scrapped: {DescribeScrapped(workOrder)}.");

            var faulted = workOrder.Fault(author, reason);
            return faulted.Success
                ? new Resolution(InspectionOutcome.Faulted, reason)
                : new Resolution(InspectionOutcome.Rejected, faulted.Error!);
        }

        var outstanding = workOrder.OutstandingQty;
        var returned = workOrder.ReturnToProduction(author, ProductionService.Truncate(
            $"Attempt {workOrder.BuildAttempt} left {outstanding} unit(s) short; returning to production."));

        return returned.Success
            ? new Resolution(InspectionOutcome.ReworkRequired,
                $"{outstanding} unit(s) short after attempt {workOrder.BuildAttempt}; rebuild required.")
            : new Resolution(InspectionOutcome.Rejected, returned.Error!);
    }

    /// <summary>
    /// Stages the outcome's announcement. Each branch publishes its <em>concrete</em> event type
    /// on purpose: the publisher serializes <c>EventEnvelope&lt;T&gt;</c>, and handing it the
    /// <see cref="IntegrationEvent"/> base type would serialize an empty payload.
    /// <para>
    /// Called before the commit since 8.1 — this writes an outbox row, it does not touch the
    /// broker, so it belongs inside the unit of work rather than after it.
    /// </para>
    /// </summary>
    private async Task PublishResolution(WorkOrder workOrder, Resolution resolution, CancellationToken cancellationToken)
    {
        switch (resolution.Outcome)
        {
            case InspectionOutcome.Passed:
                await _eventPublisher.PublishAsync(new InspectionPassed(
                    workOrder.Id,
                    workOrder.OrderedItem.ItemId,
                    workOrder.AssignedStock
                        .Where(unit => unit.Status == UnitStatus.Passed)
                        .Select(unit => unit.SerialNumber)
                        .ToList(),
                    DateTime.UtcNow), cancellationToken);
                break;

            case InspectionOutcome.ReworkRequired:
                await _eventPublisher.PublishAsync(new ReworkRequired(
                    workOrder.Id,
                    workOrder.OrderedItem.ItemId,
                    ScrappedOnAttempt(workOrder, workOrder.BuildAttempt),
                    workOrder.OutstandingQty,
                    workOrder.BuildAttempt,
                    DateTime.UtcNow), cancellationToken);
                break;

            case InspectionOutcome.Faulted:
                await _eventPublisher.PublishAsync(new WorkOrderFaulted(
                    workOrder.Id,
                    workOrder.OrderedItem.ItemId,
                    resolution.Summary,
                    workOrder.BuildAttempt,
                    DateTime.UtcNow), cancellationToken);
                break;
        }
    }

    private static IReadOnlyList<ScrappedUnit> ScrappedOnAttempt(WorkOrder workOrder, int attemptNumber) =>
        workOrder.AssignedStock
            .Where(unit => unit.Status == UnitStatus.Scrapped && unit.BuildAttempt == attemptNumber)
            .Select(unit => new ScrappedUnit(unit.SerialNumber, unit.ScrapReason ?? "unspecified"))
            .ToList();

    private static string DescribeScrapped(WorkOrder workOrder) =>
        string.Join("; ", workOrder.AssignedStock
            .Where(unit => unit.Status == UnitStatus.Scrapped)
            .Select(unit => $"{unit.SerialNumber} ({unit.ScrapReason})"));

    private InspectionResult AlreadyInspected(Guid workOrderId, int attemptNumber)
    {
        var summary = $"Attempt {attemptNumber} of work order {workOrderId} was already inspected; skipping duplicate.";
        _logger.LogInformation("Duplicate inspection skipped (idempotent): {Summary}", summary);
        return new InspectionResult(InspectionOutcome.AlreadyInspected, summary, attemptNumber);
    }

    private InspectionResult Rejected(WorkOrder workOrder, int attemptNumber, string error)
    {
        _logger.LogInformation(
            "Work order {WorkOrderId} could not be inspected (attempt {Attempt}, status {Status}): {Error}",
            workOrder.Id, attemptNumber, workOrder.CurrentStatus, error);
        return new InspectionResult(InspectionOutcome.Rejected, error, attemptNumber);
    }
}
