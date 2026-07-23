namespace ArtificeWorks.Application.Simulation;

/// <summary>
/// Every dial on the factory, as one immutable snapshot (10.2).
/// <para>
/// <strong>Why a row and not appsettings.</strong> The epic's acceptance criterion is that pacing
/// and failure rates are configurable <em>at runtime</em>, and until this story they were
/// emphatically not: three POCO singletons bound at startup, so changing
/// <c>Inspection:FailureRate</c> meant editing a file and restarting three processes. That is a
/// demo problem before it is an engineering one — the best moment in this system is "watch this"
/// followed by a visible change in behaviour, and restarting the factory to get it costs the
/// audience the thing they were watching.
/// </para>
/// <para>
/// <strong>And it is a two-process problem.</strong> A <c>PUT</c> handled by the API changes
/// nothing about what the worker does, and the worker is where inspections actually fail. Whatever
/// holds these values has to be somewhere all three hosts look — the same argument 9.2 made when
/// it put the pipeline gauges behind a shared snapshot, landing on the same answer.
/// </para>
/// <para>
/// <strong>appsettings still supplies the defaults.</strong> The row is an override layer: it is
/// created from configuration on first run and is the authority thereafter. Nothing was renamed —
/// <c>Inspection:FailureRate</c> and <c>Shipping:RefusalRate</c> keep their meaning and their
/// documentation from 6.2 and 7.3, they just stop being the last word. Deleting the row is a
/// working reset.
/// </para>
/// <para>
/// <strong>The seeds are deliberately not here.</strong> <c>Inspection:Seed</c> and
/// <c>Shipping:Seed</c> exist so a coin flip can be asserted; a live-editable seed is a mistake
/// waiting to be filed as a flake. They stay in configuration.
/// </para>
/// <para>
/// <strong>This is not failure injection.</strong> These are global dials on the whole factory.
/// Per-order targeting — <em>fail <strong>this</strong> inspection</em> — is Epic 12, needs a
/// different guard and a different blast radius, and must not be smuggled in here.
/// </para>
/// </summary>
public sealed record SimulationSettings
{
    // ------------------------------------------------------------------ pacing (10.1)

    /// <summary>
    /// Whether the outbox dispatcher routes events through the pace ladder at all. Off is the
    /// shipped default, so a fresh clone and the test suite behave exactly as Epic 9 left them.
    /// </summary>
    public bool PacingEnabled { get; init; }

    /// <summary>How long a material pick appears to take. Paid by <c>work-order.scheduled</c>.</summary>
    public double PaceSecondsScheduled { get; init; } = 5;

    /// <summary>How long a build appears to take. Paid by <c>work-order.materials-reserved</c>.</summary>
    public double PaceSecondsMaterialsReserved { get; init; } = 13;

    /// <summary>How long an inspection appears to take. Paid by <c>work-order.production-completed</c>.</summary>
    public double PaceSecondsProductionCompleted { get; init; } = 5;

    /// <summary>How long a rebuild appears to take. Paid by <c>work-order.rework-required</c>.</summary>
    public double PaceSecondsReworkRequired { get; init; } = 8;

    /// <summary>How long booking a carrier appears to take. Paid by <c>work-order.inspection-passed</c>.</summary>
    public double PaceSecondsInspectionPassed { get; init; } = 3;

    /// <summary>How long a dispatch appears to take. Paid by <c>work-order.shipment-scheduled</c>.</summary>
    public double PaceSecondsShipmentScheduled { get; init; } = 2;

    /// <summary>
    /// How far a stage's duration may wander, as a fraction of itself (0.0–1.0). Because pacing is
    /// quantized, this decides <em>which rung</em> rather than how many milliseconds — a 5s stage
    /// with 0.3 jitter lands on the 3s, 5s or 8s rung.
    /// </summary>
    public double PaceJitter { get; init; } = 0.25;

    // ------------------------------------------------------- temperament (6.2, 6.3, 7.3)

    /// <summary>Probability that any one unit fails inspection, 0.0–1.0. See 6.2.</summary>
    public double FailureRate { get; init; }

    /// <summary>Whether the inspection consumer issues verdicts automatically, or units wait for a human. See 6.2.</summary>
    public bool AutoInspect { get; init; } = true;

    /// <summary>Probability that a carrier refuses the job, 0.0–1.0. See 7.3.</summary>
    public double RefusalRate { get; init; }

    /// <summary>Whether the shipping consumer books a carrier automatically, or the visitor chooses. See 7.2.</summary>
    public bool AutoBook { get; init; } = true;

    /// <summary>Rebuilds allowed before an order is faulted. Counts attempts, not scrapped units. See 6.3.</summary>
    public int MaxRebuildAttempts { get; init; } = 3;

    // ------------------------------------------------------------- demand (10.3)

    /// <summary>
    /// Whether the simulation creates work orders on its own. <strong>Off by default</strong>,
    /// matching every other simulation-adjacent knob, so a fresh clone and the integration suite
    /// stay deterministic — orders appearing underneath a test that asserts on counts would be a
    /// cruel kind of flake.
    /// </summary>
    public bool GenerationEnabled { get; init; }

    /// <summary>Seconds between generation ticks.</summary>
    public int GenerationIntervalSeconds { get; init; } = 20;

    /// <summary>
    /// The ceiling on orders in flight. A ceiling rather than a rate limiter, because the failure
    /// this prevents is a backlog built during an outage — and a rate limiter does not prevent
    /// that, it just builds the backlog politely.
    /// </summary>
    public int MaxInFlight { get; init; } = 12;

    // ------------------------------------------------------- world lifecycle (10.4)

    /// <summary>Hours between world sweeps. Long by default: the sweep should be almost invisible.</summary>
    public int WorldSweepIntervalHours { get; init; } = 6;

    /// <summary>
    /// How old a terminal, held or faulted order must be before the sweep retires it. Generous by
    /// default, so nothing a visitor is looking at can vanish.
    /// </summary>
    public int RetireAfterHours { get; init; } = 24;

    /// <summary>What the system runs on before a row exists — Epic 9's behaviour, exactly.</summary>
    public static SimulationSettings ShippedDefaults { get; } = new();

    /// <summary>
    /// The configured duration for the stage that <paramref name="eventType"/> pays for, or
    /// <see cref="TimeSpan.Zero"/> for an event this factory does not pace.
    /// <para>
    /// Keyed by <em>routing key</em>, because the wait represents the work the <em>consumer</em> is
    /// about to do: <c>work-order.scheduled</c> is not the scheduling, it is the pick that follows.
    /// </para>
    /// </summary>
    public TimeSpan PaceFor(string eventType) => TimeSpan.FromSeconds(eventType switch
    {
        "work-order.scheduled" => PaceSecondsScheduled,
        "work-order.materials-reserved" => PaceSecondsMaterialsReserved,
        "work-order.production-completed" => PaceSecondsProductionCompleted,
        "work-order.rework-required" => PaceSecondsReworkRequired,
        "work-order.inspection-passed" => PaceSecondsInspectionPassed,
        "work-order.shipment-scheduled" => PaceSecondsShipmentScheduled,
        // work-order.created, work-order.completed and work-order.faulted are announcements, not
        // hand-offs: nothing is waiting to do work because of them, so there is nothing to pace.
        _ => 0
    });
}

/// <summary>
/// Holds the current <see cref="SimulationSettings"/>. A singleton in every host, refreshed by
/// <c>SimulationSettingsRefreshTask</c> on 10.1's scheduler and read by everything else.
/// <para>
/// <strong>Reading a knob is a field read.</strong> No request, no handler and no metric collection
/// issues a query for one — 9.2's rule for the pipeline gauges, restated for the dials. The cost is
/// that a <c>PUT</c> is not instant across hosts and should not pretend to be; making it instant
/// means either a query per decision or a broadcast, and both are worse than a few seconds' lag on
/// a demo dial.
/// </para>
/// <para>
/// Deliberately trivial, exactly like <c>PipelineSnapshotCache</c>: a single reference swap over an
/// immutable record, so readers never block and never see a torn value.
/// </para>
/// </summary>
public sealed class SimulationSettingsCache
{
    private SimulationSettings _current;
    private volatile bool _overridden;

    /// <param name="defaults">
    /// The values from appsettings, applied before the first refresh so a host that cannot reach
    /// the database still behaves as configured rather than as a blank record.
    /// </param>
    public SimulationSettingsCache(SimulationSettings? defaults = null)
    {
        _current = defaults ?? SimulationSettings.ShippedDefaults;
    }

    public SimulationSettings Current => Volatile.Read(ref _current);

    /// <summary>True once a stored row has been read — i.e. the values are the row's, not appsettings'.</summary>
    public bool IsOverridden => _overridden;

    public void Update(SimulationSettings settings)
    {
        Volatile.Write(ref _current, settings);
        _overridden = true;
    }
}
