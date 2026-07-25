using ArtificeWorks.Application.Chaos;

namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// The injected-fault registry (Epic 12) — the one write surface the whole chaos epic stands on.
/// <para>
/// Narrow on purpose: the API <see cref="Arm"/>s, the pipeline <see cref="TryConsume"/>s, and the
/// world reset <see cref="DisarmUnfired"/>s the leftovers. There is deliberately no general "list
/// faults" query yet — 12.3 adds a read model when the dashboard wants one.
/// </para>
/// </summary>
public interface IInjectedFaultRepository
{
    /// <summary>
    /// Arms a fault against one order. Idempotent by <c>(order, kind)</c>: an already-armed,
    /// unfired fault of the same kind is returned as-is rather than stacking a second row — a
    /// double-clicking visitor cannot accumulate faults.
    /// </summary>
    Task<InjectedFault> Arm(Guid workOrderId, InjectedFaultKind kind, string armedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires an armed fault of <paramref name="kind"/> against <paramref name="workOrderId"/> if
    /// one is armed and unfired, marking it fired and returning <c>true</c>; otherwise <c>false</c>.
    /// <para>
    /// <strong>Atomic and fire-once.</strong> The mark commits on its own — outside any stage
    /// transaction the caller may open around its work — so the fault is consumed exactly once even
    /// across a redelivered message, and a rolled-back stage cannot re-arm it. That property is what
    /// 12.2's transient-then-recover fault turns on; 12.1 already relies on it for "fires once".
    /// </para>
    /// </summary>
    Task<bool> TryConsume(Guid workOrderId, InjectedFaultKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears armed-but-unfired faults — the world-reset sweep (10.4). An armed fault must not
    /// outlive a reset; a <em>fired</em> fault is left as provenance and ages out with its order's
    /// retirement cascade.
    /// </summary>
    Task<int> DisarmUnfired(CancellationToken cancellationToken = default);
}
