namespace ArtificeWorks.Application.Production;

/// <summary>How one production attempt ended.</summary>
public enum ProductionOutcome
{
    /// <summary>Units were built and the hand-off to inspection published.</summary>
    Built,

    /// <summary>This attempt had already been built — a duplicate delivery, safely ignored.</summary>
    AlreadyBuilt,

    /// <summary>No work order with that id. Nothing to do and nothing to retry against.</summary>
    WorkOrderNotFound,

    /// <summary>
    /// The order could not be built in its current state (held, cancelled, faulted, or the
    /// attempt arrived out of sequence). A business outcome, not a fault: the message acks.
    /// </summary>
    Rejected
}

/// <param name="Summary">Human-readable description of what happened, as written to state history.</param>
/// <param name="SerialNumbers">The units this attempt built, empty unless <see cref="ProductionOutcome.Built"/>.</param>
public sealed record ProductionResult(
    ProductionOutcome Outcome,
    string Summary,
    int AttemptNumber,
    IReadOnlyList<Guid>? SerialNumbers = null);
