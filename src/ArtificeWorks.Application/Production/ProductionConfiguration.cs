namespace ArtificeWorks.Application.Production;

/// <summary>
/// Production knobs, bound from the <c>Production</c> configuration section.
/// <para>
/// A plain POCO rather than <c>IOptions&lt;T&gt;</c>: the Application layer deliberately depends
/// on almost nothing, and tests construct it directly.
/// </para>
/// </summary>
public sealed class ProductionConfiguration
{
    public const string SectionName = "Production";

    /// <summary>
    /// How many times a work order may be sent back to rebuild a shortfall before the pipeline
    /// gives up and routes it to Fault. Counts <em>attempts</em>, not scrapped units: an attempt
    /// that scraps three units has used one attempt. The initial build is not a rebuild, so the
    /// maximum number of production attempts is this plus one.
    /// </summary>
    public int MaxRebuildAttempts { get; set; } = 3;
}
