using ArtificeWorks.Application.Simulation;

using Microsoft.Extensions.Options;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Decides whether an event on its way to the wire should rest in a delay queue first, and on
/// which rung (10.1).
/// </summary>
public interface IPacePolicy
{
    /// <summary>
    /// The rung to hold <paramref name="eventType"/> on, or <c>null</c> for "straight to
    /// <c>artifice.events</c>". Null is what off returns for everything, and off is the shipped
    /// default — so with no configuration the outbox dispatcher's behaviour is byte-for-byte what
    /// 8.1 shipped.
    /// </summary>
    PaceDecision? Decide(string eventType);
}

/// <summary>The chosen rung, its exchange, and the delay it actually applies once quantized.</summary>
public sealed record PaceDecision(int Rung, string Exchange, string Label, int DelayMs);

/// <summary>
/// The real policy: read the stage's configured duration off 10.2's cached settings, apply jitter,
/// and snap to the nearest rung.
/// <para>
/// <strong>Jitter picks a rung, not a millisecond count.</strong> The ladder is the quantizer, so
/// the honest way to vary a stage is to let the jittered duration land on a neighbouring rung. At
/// demo resolution — a 5s stage that sometimes takes 3s and sometimes 8s — that is exactly the
/// variation the story asked for, and it costs no extra queues.
/// </para>
/// <para>
/// Singleton, and the generator is locked, for the same reason <c>RandomVerdictSource</c> and
/// <c>ConfiguredCarrierBooking</c> are: one reproducible sequence per process, and the outbox
/// dispatcher may call this from more than one thread.
/// </para>
/// </summary>
public sealed class PacePolicy : IPacePolicy
{
    private readonly PaceConfiguration _config;
    private readonly SimulationSettingsCache _settings;
    private readonly Random _random;
    private readonly Lock _gate = new();

    public PacePolicy(IOptions<PaceConfiguration> config, SimulationSettingsCache settings, int? seed = null)
    {
        _config = config.Value;
        _settings = settings;
        _random = seed is int value ? new Random(value) : new Random();
    }

    public PaceDecision? Decide(string eventType)
    {
        var settings = _settings.Current;
        if (!settings.PacingEnabled)
        {
            return null;
        }

        var configured = settings.PaceFor(eventType);
        if (configured <= TimeSpan.Zero)
        {
            return null;
        }

        var rung = _config.RungFor(Jittered(configured, settings.PaceJitter));
        if (rung is not int index)
        {
            return null;
        }

        return new PaceDecision(
            index, _config.ExchangeFor(index), _config.LabelFor(index), _config.MillisecondsFor(index));
    }

    private TimeSpan Jittered(TimeSpan duration, double jitter)
    {
        if (jitter <= 0)
        {
            return duration;
        }

        double factor;
        lock (_gate)
        {
            // Uniform in [1 - jitter, 1 + jitter]. Clamped below at a tenth so a pathological
            // jitter setting cannot produce a negative duration and silently mean "don't pace".
            factor = 1 + ((_random.NextDouble() * 2) - 1) * Math.Clamp(jitter, 0, 1);
        }

        return duration * Math.Max(0.1, factor);
    }
}
