namespace ArtificeWorks.Infrastructure.Simulation;

/// <summary>
/// The <c>Simulation</c> configuration section — the parts of the simulation that are <em>not</em>
/// runtime dials (10.1–10.4).
/// <para>
/// <strong>The split is deliberate.</strong> Anything a visitor might reasonably want to turn
/// during a demo lives on 10.2's settings row; anything that is deployment shape — how often to
/// re-read the row, where the API is, whether this process generates at all — stays here, in
/// appsettings, where it is a deployment decision rather than a button.
/// </para>
/// </summary>
public sealed class SimulationConfiguration
{
    public const string SectionName = "Simulation";

    /// <summary>
    /// How often each host re-reads the settings row. Five seconds: fast enough that a demo dial
    /// feels live, slow enough that three hosts polling one row is not a workload.
    /// </summary>
    public int SettingsRefreshMs { get; set; } = 5_000;

    /// <summary>
    /// The API's base address, for 10.3's generator. It creates orders <strong>over HTTP</strong>,
    /// through the same front door as everyone else — a direct write would skip 8.4's idempotency
    /// filter, the DTO validation and the outbox row that all three exist to guarantee.
    /// </summary>
    public string ApiBaseAddress { get; set; } = "http://localhost:5000";

    /// <summary>
    /// A hard off switch for this process's generator, independent of the settings row. The row
    /// says whether the factory <em>should</em> generate; this says whether this deployment is
    /// allowed to — so a second simulation host started by accident cannot double the demand.
    /// </summary>
    public bool GeneratorEnabled { get; set; } = true;

    /// <summary>A hard off switch for this process's world sweep, on the same reasoning.</summary>
    public bool WorldSweepEnabled { get; set; } = true;

    /// <summary>Optional seed for the generator's product and quantity choices, so a test can assert on them.</summary>
    public int? Seed { get; set; }
}
