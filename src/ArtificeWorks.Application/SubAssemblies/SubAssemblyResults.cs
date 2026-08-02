using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.SubAssemblies;

/// <summary>What asking the factory to make its own missing parts came to.</summary>
public enum SubAssemblyRequestOutcome
{
    /// <summary>Nothing short was manufactured here — every shortage is a bought part.</summary>
    NothingToMake,

    /// <summary>At least one child work order was created and scheduled.</summary>
    Requested,

    /// <summary>
    /// Every made shortage already has a live child order on this attempt. The parent waits; a
    /// redelivery lands here, and so does a genuine re-pick that ran while a sibling was still
    /// building.
    /// </summary>
    AlreadyRequested,

    /// <summary>
    /// The chain is already <see cref="WorkOrder.MaxSubAssemblyDepth"/> deep. Nothing is spawned and
    /// the caller faults the order instead — a cyclic or absurd catalog stops here rather than
    /// generating work forever.
    /// </summary>
    TooDeep
}

/// <summary>
/// The child work orders a short pick <em>would</em> raise, built and staged but not yet committed.
/// <para>
/// A plan rather than a result because the parent's hold has to name what it is waiting for, and the
/// hold and the children have to land in the same save. So the children are constructed (attached to
/// the parent's tracked graph, their <c>created</c> and <c>scheduled</c> events staged in the
/// outbox), handed back for the caller to describe, and committed by the caller's single
/// <c>SaveChanges</c>.
/// </para>
/// </summary>
/// <param name="Children">The unsaved child orders, for the caller to commit.</param>
/// <param name="Requested">The same children, described — for the log and the pick's result.</param>
public sealed record SubAssemblyPlan(
    SubAssemblyRequestOutcome Outcome,
    string Summary,
    IReadOnlyList<WorkOrder> Children,
    IReadOnlyList<RequestedSubAssembly> Requested)
{
    public static SubAssemblyPlan Nothing(string summary) =>
        new(SubAssemblyRequestOutcome.NothingToMake, summary, [], []);

    public static SubAssemblyPlan AlreadyRequested(string summary) =>
        new(SubAssemblyRequestOutcome.AlreadyRequested, summary, [], []);

    public static SubAssemblyPlan TooDeep(string summary) =>
        new(SubAssemblyRequestOutcome.TooDeep, summary, [], []);
}

/// <param name="ComponentId">The component the child builds.</param>
/// <param name="ProductId">The sub-assembly product that builds it.</param>
/// <param name="Quantity">The shortfall: demand minus what was on the shelf.</param>
public sealed record RequestedSubAssembly(
    Guid WorkOrderId,
    string ComponentId,
    string ProductId,
    uint Quantity);

/// <summary>What releasing a parent whose child has finished came to.</summary>
public enum ParentReleaseOutcome
{
    /// <summary>The completed order has no parent — an ordinary customer order finishing.</summary>
    NotASubAssembly,

    /// <summary>The parent was released and re-scheduled to pick again.</summary>
    Released,

    /// <summary>
    /// The parent is not held — a sibling released it already, or a visitor did. Nothing to do,
    /// and deliberately not an error: the last child to finish is the one that resumes it.
    /// </summary>
    NotHeld,

    /// <summary>The parent has been retired, cancelled or was never there.</summary>
    ParentGone
}

public sealed record ParentReleaseResult(ParentReleaseOutcome Outcome, string Summary);
