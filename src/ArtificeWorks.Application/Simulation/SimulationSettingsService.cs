using ArtificeWorks.Application.Interfaces;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Simulation;

public enum SimulationSettingsOutcome
{
    Applied,
    OutOfRange
}

/// <param name="TakesEffectSeconds">
/// How long until every host is running on this. The response says so out loud because the
/// snapshot is deliberately eventually consistent (10.2) — a <c>PUT</c> is not instant across three
/// processes and should not pretend to be.
/// </param>
public sealed record SimulationSettingsResult(
    SimulationSettingsOutcome Outcome,
    SimulationSettings Settings,
    string Summary,
    int TakesEffectSeconds = 0);

/// <summary>
/// Validates and applies a change to the factory's dials (10.2).
/// <para>
/// <strong>Bounds are a shared-world courtesy, not paranoia.</strong> This is a demo a stranger can
/// reach; a pacing duration of six hours or a failure rate of 40 would break the thing quietly
/// rather than loudly. The gate that would stop a stranger touching this at all is the admin auth
/// deferred since Epic 3 — which is exactly why the endpoint lives under <c>/system</c>.
/// </para>
/// </summary>
public sealed class SimulationSettingsService
{
    /// <summary>The widest a stage may be paced. Long enough to be silly, short enough not to strand an order for a day.</summary>
    public const double MaxPaceSeconds = 300;

    private readonly ISimulationSettingsRepository _repository;
    private readonly SimulationSettingsCache _cache;
    private readonly ILogger<SimulationSettingsService> _logger;

    public SimulationSettingsService(
        ISimulationSettingsRepository repository,
        SimulationSettingsCache cache,
        ILogger<SimulationSettingsService> logger)
    {
        _repository = repository;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>What is in force right now — the cached snapshot, not a query.</summary>
    public SimulationSettings Current => _cache.Current;

    public bool IsOverridden => _cache.IsOverridden;

    public async Task<SimulationSettingsResult> Update(
        SimulationSettings requested,
        string updatedBy,
        int takesEffectSeconds,
        CancellationToken cancellationToken = default)
    {
        if (Validate(requested) is string problem)
        {
            // Nothing is written and nothing is cached: a rejected PUT must leave the live value
            // exactly as it was, which is the half of "out of range" worth testing.
            return new SimulationSettingsResult(SimulationSettingsOutcome.OutOfRange, Current, problem);
        }

        var before = Current;
        var stored = await _repository.Save(requested, updatedBy, cancellationToken);

        // Apply locally at once rather than waiting for this host's own refresh, so the response a
        // caller reads back is the value they set. The other two hosts converge on their next tick.
        _cache.Update(stored);

        // Information with the before and the after (9.3's argument): "someone set the failure rate
        // to 0.9 four minutes ago" is the same kind of fact as a hold or a retry — it explains a
        // later surprise, and there is no other record that it happened.
        _logger.LogInformation(
            "Simulation settings changed by {UpdatedBy}: pacing {PacingWas}→{PacingNow}, failure rate {FailureWas}→{FailureNow}, "
            + "refusal rate {RefusalWas}→{RefusalNow}, generation {GenerationWas}→{GenerationNow}.",
            updatedBy, before.PacingEnabled, stored.PacingEnabled, before.FailureRate, stored.FailureRate,
            before.RefusalRate, stored.RefusalRate, before.GenerationEnabled, stored.GenerationEnabled);

        return new SimulationSettingsResult(
            SimulationSettingsOutcome.Applied, stored,
            "Settings applied.", takesEffectSeconds);
    }

    /// <summary>Writes the configured defaults if nobody has ever set anything, then caches whatever is in force.</summary>
    public async Task<SimulationSettings> SeedAndCache(
        SimulationSettings defaults, CancellationToken cancellationToken = default)
    {
        var settings = await _repository.SeedIfMissing(defaults, cancellationToken);
        _cache.Update(settings);
        return settings;
    }

    /// <summary>The rejection message, or null when everything is in range.</summary>
    public static string? Validate(SimulationSettings settings)
    {
        if (settings.FailureRate is < 0 or > 1)
        {
            return $"Inspection failure rate must be between 0.0 and 1.0; got {settings.FailureRate}.";
        }
        if (settings.RefusalRate is < 0 or > 1)
        {
            return $"Carrier refusal rate must be between 0.0 and 1.0; got {settings.RefusalRate}.";
        }
        if (settings.PaceJitter is < 0 or > 1)
        {
            return $"Pace jitter must be between 0.0 and 1.0; got {settings.PaceJitter}.";
        }
        if (settings.MaxRebuildAttempts is < 0 or > 20)
        {
            return $"Max rebuild attempts must be between 0 and 20; got {settings.MaxRebuildAttempts}.";
        }
        if (settings.GenerationIntervalSeconds < 1)
        {
            return $"Generation interval must be at least 1 second; got {settings.GenerationIntervalSeconds}.";
        }
        if (settings.MaxInFlight is < 0 or > 10_000)
        {
            return $"Max in-flight orders must be between 0 and 10000; got {settings.MaxInFlight}.";
        }
        if (settings.WorldSweepIntervalHours < 1)
        {
            return $"World sweep interval must be at least 1 hour; got {settings.WorldSweepIntervalHours}.";
        }
        if (settings.RetireAfterHours < 1)
        {
            return $"Retire-after must be at least 1 hour; got {settings.RetireAfterHours}.";
        }

        foreach (var (stage, seconds) in new (string Stage, double Seconds)[]
        {
            ("scheduled", settings.PaceSecondsScheduled),
            ("materials-reserved", settings.PaceSecondsMaterialsReserved),
            ("production-completed", settings.PaceSecondsProductionCompleted),
            ("rework-required", settings.PaceSecondsReworkRequired),
            ("inspection-passed", settings.PaceSecondsInspectionPassed),
            ("shipment-scheduled", settings.PaceSecondsShipmentScheduled),
        })
        {
            // Zero is legal and means "don't pace this stage" — which is how one stage is turned
            // off without a second flag per stage.
            if (seconds < 0 || seconds > MaxPaceSeconds)
            {
                return $"Pacing for {stage} must be between 0 and {MaxPaceSeconds} seconds; got {seconds}.";
            }
        }

        return null;
    }
}
