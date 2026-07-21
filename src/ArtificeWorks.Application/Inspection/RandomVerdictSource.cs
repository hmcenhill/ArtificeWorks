using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Inspection;

/// <summary>
/// A coin flip weighted by <see cref="InspectionConfiguration.FailureRate"/>. That is the whole
/// of it, on purpose.
/// <para>
/// Registered as a singleton so a configured <see cref="InspectionConfiguration.Seed"/> produces
/// one reproducible sequence across the process rather than restarting per message; the
/// generator is therefore locked, since consumers may verdict from more than one thread.
/// </para>
/// </summary>
public sealed class RandomVerdictSource : IVerdictSource
{
    private readonly InspectionConfiguration _config;
    private readonly Random _random;
    private readonly Lock _gate = new();

    public RandomVerdictSource(InspectionConfiguration config)
    {
        _config = config;
        _random = config.Seed is int seed ? new Random(seed) : new Random();
    }

    public UnitVerdict Verdict(StockKeepingUnit unit)
    {
        if (_config.FailureRate <= 0)
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

        return roll < _config.FailureRate
            ? new UnitVerdict(Passed: false, Reason: _config.AutoFailureReason)
            : new UnitVerdict(Passed: true);
    }
}
