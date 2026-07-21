namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// Where one serialized unit is in its own short life. This is deliberately separate from
/// <see cref="WorkOrderStatus"/>: an order is in Inspection as a whole, but each unit inside
/// it passes or fails on its own, which is what makes serialization mean anything.
/// </summary>
public enum UnitStatus
{
    /// <summary>Produced and awaiting a verdict.</summary>
    Built,

    /// <summary>Inspected and accepted. Counts towards the order's fulfilled quantity.</summary>
    Passed,

    /// <summary>Inspected and rejected, with a reason. Never counts, never re-inspected,
    /// and kept forever as the record of what went wrong.</summary>
    Scrapped
}
