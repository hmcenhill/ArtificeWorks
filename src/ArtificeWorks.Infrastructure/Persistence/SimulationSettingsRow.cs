using ArtificeWorks.Application.Simulation;

namespace ArtificeWorks.Infrastructure.Persistence;

/// <summary>
/// The single row behind <see cref="SimulationSettings"/> (10.2).
/// <para>
/// <strong>Singleton by construction</strong>: the key is fixed at <see cref="SingletonId"/> and a
/// <c>CHECK (id = 1)</c> in the schema makes a second row impossible. There is no "which settings
/// are in force?" question to get wrong, and no scenario where two hosts read different rows.
/// </para>
/// <para>
/// Plumbing rather than domain, so it lives here next to <c>OutboxMessage</c> and
/// <c>IdempotencyRecord</c> rather than in Domain with the aggregates: it describes how the factory
/// is <em>tuned</em>, not what the factory makes.
/// </para>
/// </summary>
public class SimulationSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; private set; } = SingletonId;

    public bool PacingEnabled { get; set; }
    public double PaceSecondsScheduled { get; set; }
    public double PaceSecondsMaterialsReserved { get; set; }
    public double PaceSecondsProductionCompleted { get; set; }
    public double PaceSecondsReworkRequired { get; set; }
    public double PaceSecondsInspectionPassed { get; set; }
    public double PaceSecondsShipmentScheduled { get; set; }
    public double PaceJitter { get; set; }

    public double FailureRate { get; set; }
    public bool AutoInspect { get; set; }
    public double RefusalRate { get; set; }
    public bool AutoBook { get; set; }
    public int MaxRebuildAttempts { get; set; }

    public bool GenerationEnabled { get; set; }
    public int GenerationIntervalSeconds { get; set; }
    public int MaxInFlight { get; set; }

    public int WorldSweepIntervalHours { get; set; }
    public int RetireAfterHours { get; set; }

    /// <summary>When this row was last changed, and by whom. 9.3's argument: a surprise four minutes from now needs a fact from four minutes ago.</summary>
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;

    private SimulationSettingsRow() { }

    public static SimulationSettingsRow From(SimulationSettings settings, string updatedBy)
    {
        var row = new SimulationSettingsRow();
        row.Apply(settings, updatedBy);
        return row;
    }

    public void Apply(SimulationSettings settings, string updatedBy)
    {
        PacingEnabled = settings.PacingEnabled;
        PaceSecondsScheduled = settings.PaceSecondsScheduled;
        PaceSecondsMaterialsReserved = settings.PaceSecondsMaterialsReserved;
        PaceSecondsProductionCompleted = settings.PaceSecondsProductionCompleted;
        PaceSecondsReworkRequired = settings.PaceSecondsReworkRequired;
        PaceSecondsInspectionPassed = settings.PaceSecondsInspectionPassed;
        PaceSecondsShipmentScheduled = settings.PaceSecondsShipmentScheduled;
        PaceJitter = settings.PaceJitter;

        FailureRate = settings.FailureRate;
        AutoInspect = settings.AutoInspect;
        RefusalRate = settings.RefusalRate;
        AutoBook = settings.AutoBook;
        MaxRebuildAttempts = settings.MaxRebuildAttempts;

        GenerationEnabled = settings.GenerationEnabled;
        GenerationIntervalSeconds = settings.GenerationIntervalSeconds;
        MaxInFlight = settings.MaxInFlight;

        WorldSweepIntervalHours = settings.WorldSweepIntervalHours;
        RetireAfterHours = settings.RetireAfterHours;

        UpdatedUtc = DateTime.UtcNow;
        UpdatedBy = updatedBy;
    }

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
}
