namespace ArtificeWorks.Application.Inspection;

/// <summary>Inspection knobs, bound from the <c>Inspection</c> configuration section.</summary>
public sealed class InspectionConfiguration
{
    public const string SectionName = "Inspection";

    /// <summary>
    /// Probability that any one unit fails inspection, 0.0–1.0. Defaults to <c>0.0</c> —
    /// everything passes — so an unattended factory runs from creation to Delivery with no
    /// human action. Raise it to watch the rework loop work.
    /// </summary>
    public double FailureRate { get; set; }

    /// <summary>
    /// Optional seed for the verdict source's random number generator. Set it and the sequence
    /// of verdicts is reproducible, which is the only way to assert on a coin flip.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>
    /// Whether the inspection consumer issues verdicts automatically. Turn it off and units
    /// reach Inspection and wait for a human to call the verdict endpoint — the demo's decision
    /// moment. The order-level outcome is resolved the same way in both cases.
    /// </summary>
    public bool AutoInspect { get; set; } = true;

    /// <summary>The reason recorded against a unit the auto-inspector rejects.</summary>
    public string AutoFailureReason { get; set; } = "Failed automated inspection.";
}
