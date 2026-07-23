using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Inspection;

/// <summary>
/// A coin flip weighted by the current failure rate. That is the whole of it, on purpose.
/// <para>
/// Registered as a singleton so a configured <see cref="InspectionConfiguration.Seed"/> produces
/// one reproducible sequence across the process rather than restarting per message; the
/// generator is therefore locked, since consumers may verdict from more than one thread.
/// </para>
/// <para>
/// <strong>The rate comes from 10.2's cached settings, the seed does not.</strong> Turning the
/// failure rate up while the factory runs is the whole point of that story; a live-editable
/// <em>seed</em> is a mistake waiting to be filed as a flake, so it stays in configuration. When no
/// cache is supplied — a unit test constructing this directly — the configured rate is used, which
/// is also what the cache holds before its first refresh.
/// </para>
/// </summary>
public sealed class RandomVerdictSource : IVerdictSource
{
    private readonly InspectionConfiguration _config;
    private readonly SimulationSettingsCache? _settings;
    private readonly Random _random;
    private readonly Lock _gate = new();

    public RandomVerdictSource(InspectionConfiguration config, SimulationSettingsCache? settings = null)
    {
        _config = config;
        _settings = settings;
        _random = config.Seed is int seed ? new Random(seed) : new Random();
    }

    private double FailureRate => _settings?.Current.FailureRate ?? _config.FailureRate;

    public UnitVerdict Verdict(StockKeepingUnit unit)
    {
        var failureRate = FailureRate;

        if (failureRate <= 0)
        {
            // The default. Short-circuited so an unattended factory doesn't burn entropy, and
            // so "FailureRate 0 means everything passes" is true by construction rather than
            // by the generator never happening to return exactly 0.
            return new UnitVerdict(Passed: true);
        }

        double roll;
        lock (_gate)
        {
            roll = _random.NextDouble();
        }

        return roll < failureRate
            ? new UnitVerdict(Passed: false, Reason: _config.AutoFailureReason)
            : new UnitVerdict(Passed: true);
    }
}
