using ArtificeWorks.Application.Simulation;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// The factory's dials on the wire (10.2), as read by <c>GET /system/simulation</c> and written by
/// <c>PUT</c>.
/// <para>
/// <strong>It reports the rung each pacing duration resolved to, not just the duration.</strong>
/// Pacing is quantized (10.1), so setting 5s and 6s may produce identical behaviour and a message
/// already sitting in a delay queue keeps its old timing. Both are correct; both look like a bug if
/// the endpoint doesn't say so.
/// </para>
/// </summary>
public sealed record SimulationSettingsDto
{
    public bool PacingEnabled { get; init; }
    public double PaceSecondsScheduled { get; init; }
    public double PaceSecondsMaterialsReserved { get; init; }
    public double PaceSecondsProductionCompleted { get; init; }
    public double PaceSecondsReworkRequired { get; init; }
    public double PaceSecondsInspectionPassed { get; init; }
    public double PaceSecondsShipmentScheduled { get; init; }
    public double PaceJitter { get; init; }

    public double FailureRate { get; init; }
    public bool AutoInspect { get; init; }
    public double RefusalRate { get; init; }
    public bool AutoBook { get; init; }
    public int MaxRebuildAttempts { get; init; }

    public bool GenerationEnabled { get; init; }
    public int GenerationIntervalSeconds { get; init; }
    public int MaxInFlight { get; init; }

    public int WorldSweepIntervalHours { get; init; }
    public int RetireAfterHours { get; init; }

    /// <summary><c>configured</c> (appsettings, no row ever written) or <c>overridden</c> (the row is in force).</summary>
    public string Source { get; init; } = "configured";

    /// <summary>
    /// The rung each stage's duration snaps to, keyed by routing key — <c>{"work-order.scheduled":
    /// "5s"}</c>. Absent when pacing is off. This is the field that stops quantization looking like
    /// a bug.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ResolvedRungs { get; init; }

    /// <summary>
    /// How long until every host is running on these values. The snapshot is deliberately
    /// eventually consistent, so a <c>PUT</c> says when it lands rather than implying "now".
    /// </summary>
    public int TakesEffectWithinSeconds { get; init; }

    public SimulationSettings ToSettings() => new()
    {
        PacingEnabled = PacingEnabled,
        PaceSecondsScheduled = PaceSecondsScheduled,
        PaceSecondsMaterialsReserved = PaceSecondsMaterialsReserved,
        PaceSecondsProductionCompleted = PaceSecondsProductionCompleted,
        PaceSecondsReworkRequired = PaceSecondsReworkRequired,
        PaceSecondsInspectionPassed = PaceSecondsInspectionPassed,
        PaceSecondsShipmentScheduled = PaceSecondsShipmentScheduled,
        PaceJitter = PaceJitter,

        FailureRate = FailureRate,
        AutoInspect = AutoInspect,
        RefusalRate = RefusalRate,
        AutoBook = AutoBook,
        MaxRebuildAttempts = MaxRebuildAttempts,

        GenerationEnabled = GenerationEnabled,
        GenerationIntervalSeconds = GenerationIntervalSeconds,
        MaxInFlight = MaxInFlight,

        WorldSweepIntervalHours = WorldSweepIntervalHours,
        RetireAfterHours = RetireAfterHours,
    };

    public static SimulationSettingsDto From(
        SimulationSettings settings,
        bool overridden,
        IReadOnlyDictionary<string, string>? resolvedRungs = null,
        int takesEffectWithinSeconds = 0) => new()
        {
            PacingEnabled = settings.PacingEnabled,
            PaceSecondsScheduled = settings.PaceSecondsScheduled,
            PaceSecondsMaterialsReserved = settings.PaceSecondsMaterialsReserved,
            PaceSecondsProductionCompleted = settings.PaceSecondsProductionCompleted,
            PaceSecondsReworkRequired = settings.PaceSecondsReworkRequired,
            PaceSecondsInspectionPassed = settings.PaceSecondsInspectionPassed,
            PaceSecondsShipmentScheduled = settings.PaceSecondsShipmentScheduled,
            PaceJitter = settings.PaceJitter,

            FailureRate = settings.FailureRate,
            AutoInspect = settings.AutoInspect,
            RefusalRate = settings.RefusalRate,
            AutoBook = settings.AutoBook,
            MaxRebuildAttempts = settings.MaxRebuildAttempts,

            GenerationEnabled = settings.GenerationEnabled,
            GenerationIntervalSeconds = settings.GenerationIntervalSeconds,
            MaxInFlight = settings.MaxInFlight,

            WorldSweepIntervalHours = settings.WorldSweepIntervalHours,
            RetireAfterHours = settings.RetireAfterHours,

            Source = overridden ? "overridden" : "configured",
            ResolvedRungs = resolvedRungs,
            TakesEffectWithinSeconds = takesEffectWithinSeconds,
        };
}
