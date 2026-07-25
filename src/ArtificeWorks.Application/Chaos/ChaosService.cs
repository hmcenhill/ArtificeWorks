using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Chaos;

public enum ChaosArmOutcome
{
    Armed,
    TargetNotFound,
    TargetNotInjectable,
}

/// <param name="Fault">The armed fault, present only when <paramref name="Outcome"/> is <see cref="ChaosArmOutcome.Armed"/>.</param>
public sealed record ChaosArmResult(ChaosArmOutcome Outcome, InjectedFault? Fault, string Summary);

/// <summary>
/// Arms a fault against one order, and fires it for the pipeline (Epic 12).
/// <para>
/// <strong>The blast radius is bounded at the door.</strong> Every fault names one
/// <c>WorkOrderId</c> and fires against that order only — the deliberate opposite of 10.2's dials,
/// which retune the whole factory. Arming refuses an order that cannot be broken (terminal, or
/// already faulted) and a kind/stage mismatch it can detect cheaply, so the "cannot corrupt the
/// shared world" guarantee holds before a row is ever written rather than only by convention.
/// </para>
/// <para>
/// <strong>A fault is data the pipeline consults, not a back door that bypasses it.</strong> The
/// inspection worker reads an armed <see cref="InjectedFaultKind.FailInspection"/> the same way it
/// reads the failure rate, and the picking worker reads a
/// <see cref="InjectedFaultKind.TransientOnce"/> or <see cref="InjectedFaultKind.Poison"/> before it
/// draws stock (12.2). Recovery then runs entirely on the existing rework/Fault loop, retry ladder
/// and parked queue. There is no "chaos mode" branch anywhere in the pipeline.
/// </para>
/// </summary>
public sealed class ChaosService
{
    private readonly IInjectedFaultRepository _faults;
    private readonly IWorkOrderRepository _workOrders;
    private readonly ILogger<ChaosService> _logger;

    public ChaosService(
        IInjectedFaultRepository faults,
        IWorkOrderRepository workOrders,
        ILogger<ChaosService> logger)
    {
        _faults = faults;
        _workOrders = workOrders;
        _logger = logger;
    }

    public async Task<ChaosArmResult> Arm(
        Guid workOrderId, InjectedFaultKind kind, string armedBy, CancellationToken cancellationToken = default)
    {
        var order = await _workOrders.Get(workOrderId);
        if (order is null)
        {
            return new ChaosArmResult(ChaosArmOutcome.TargetNotFound, null,
                $"No work order found with id {workOrderId}.");
        }

        if (!IsInjectable(order.CurrentStatus, kind, out var reason))
        {
            return new ChaosArmResult(ChaosArmOutcome.TargetNotInjectable, null, reason);
        }

        var fault = await _faults.Arm(workOrderId, kind, armedBy, cancellationToken);

        // A warning, always: an order that fails "for no reason" four minutes from now needs a fact
        // from four minutes ago, and "a visitor armed this one to fail" is exactly that fact.
        _logger.LogWarning(
            "Chaos armed by {ArmedBy}: {Kind} against work order {WorkOrderId}.",
            armedBy, kind, workOrderId);

        return new ChaosArmResult(ChaosArmOutcome.Armed, fault,
            $"{kind} armed against work order {workOrderId}.");
    }

    /// <summary>
    /// The pipeline's consult: fire an armed fault of <paramref name="kind"/> against this order,
    /// exactly once. A thin pass-through to the registry, whose atomic mark is the fire-once
    /// guarantee — see <see cref="IInjectedFaultRepository.TryConsume"/>.
    /// </summary>
    public Task<bool> TryConsume(Guid workOrderId, InjectedFaultKind kind, CancellationToken cancellationToken = default)
        => _faults.TryConsume(workOrderId, kind, cancellationToken);

    /// <summary>
    /// Whether a fault of this kind can meaningfully be armed against an order in this state. Cheap,
    /// state-only checks — the bounded blast radius the epic promises, enforced at the door.
    /// </summary>
    private static bool IsInjectable(WorkOrderStatus status, InjectedFaultKind kind, out string reason)
    {
        // A finished order has nothing left to route through a recovery path.
        if (status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled)
        {
            reason = $"Work order is {status} and cannot be broken.";
            return false;
        }

        // Fault is already the give-up state; breaking it again is meaningless.
        if (status is WorkOrderStatus.Fault)
        {
            reason = "Work order is already faulted; there is nothing left to break.";
            return false;
        }

        // The one cheap kind/stage guard 12.1 has: a fail-inspection fault can only bite before the
        // order clears inspection. Once it has reached Delivery there is no inspection left to fail.
        if (kind is InjectedFaultKind.FailInspection && status is WorkOrderStatus.Delivery)
        {
            reason = "Work order has already passed inspection and reached Delivery; there is no inspection left to fail.";
            return false;
        }

        // 12.2's two broker faults fire at the picking stage, which runs on work-order.scheduled. Once
        // the order has moved into production or beyond, that consult point is behind it and an armed
        // fault would sit unfired until the world reset swept it — so refuse it at the door rather than
        // arm a dud. (Intake, Scheduled and OnHold are all at or before the pick, so all still fire.)
        if (kind is InjectedFaultKind.TransientOnce or InjectedFaultKind.Poison
            && status is WorkOrderStatus.InProcess or WorkOrderStatus.Inspection or WorkOrderStatus.Delivery)
        {
            reason = $"Work order is {status} and has already passed the picking stage, where a worker fault fires.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
