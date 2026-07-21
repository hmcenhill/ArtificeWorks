namespace ArtificeWorks.Application.Materials;

/// <summary>How a picking attempt for one work order ended.</summary>
public enum PickOutcome
{
    /// <summary>Materials were reserved and the pick recorded.</summary>
    Picked,

    /// <summary>Stock was short; the order was placed OnHold with a reason and nothing was drawn.</summary>
    InsufficientStock,

    /// <summary>This order was already picked — a duplicate delivery, safely ignored.</summary>
    AlreadyPicked,

    /// <summary>No work order with that id. Nothing to do and nothing to retry against.</summary>
    WorkOrderNotFound,

    /// <summary>The product has no BOM, so there is nothing to reserve.</summary>
    NoBillOfMaterials
}

/// <param name="Summary">Human-readable description of what happened, as written to state history.</param>
public sealed record PickResult(
    PickOutcome Outcome,
    string Summary,
    IReadOnlyList<Domain.Models.Materials.ComponentDemand>? Reserved = null);
