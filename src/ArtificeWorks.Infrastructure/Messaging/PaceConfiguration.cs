namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// The pace ladder (10.1), bound from the <c>Pace</c> configuration section. Structurally a twin
/// of <see cref="RetryConfiguration"/>, and that is the point.
/// <para>
/// <strong>The delay lives in the broker, not in a handler.</strong> 8.2 rejected in-process
/// backoff for retries because prefetch is 1 — a sleeping handler is not a slow order, it is a
/// stopped factory, with every other order queued behind it and a message held unacked the whole
/// time. A <c>Task.Delay</c> in <c>ProductionService</c> would reintroduce exactly that, and would
/// do it on the <em>happy path</em> rather than the failure path. So a paced event is published to
/// a TTL'd queue with no consumer, and dead-letters into <c>artifice.events</c> when the TTL
/// expires.
/// </para>
/// <para>
/// <strong>Uniform TTL per queue, hence a ladder.</strong> RabbitMQ only ever checks the message at
/// the head of a queue for expiry, so per-message TTL on a shared queue head-of-line blocks: a
/// 34-second message at the head holds a 2-second message behind it for 34 seconds. One queue per
/// duration removes the problem by construction. The alternative — the community
/// delayed-message-exchange plugin — buys true per-message delay at the cost of a custom RabbitMQ
/// image to build and self-host in M7, for millisecond precision on a number nobody can perceive.
/// </para>
/// <para>
/// <strong>Pacing is therefore quantized.</strong> A configured duration selects the nearest rung;
/// jitter selects <em>which rung</em>, not how many milliseconds. Small edits to a duration may not
/// move anything, which is correct and looks like a bug unless it is said out loud — which is why
/// 10.2's endpoint reports the rung a duration resolved to, not just the duration.
/// </para>
/// </summary>
public sealed class PaceConfiguration
{
    public const string SectionName = "Pace";

    /// <summary>Fibonacci-ish and log-spaced, so a full five-stage order takes a minute or two rather than an afternoon.</summary>
    private static readonly int[] ShippedRungsMs = [1_000, 2_000, 3_000, 5_000, 8_000, 13_000, 21_000, 34_000];

    /// <summary>
    /// One rung per delay, in milliseconds, as configured.
    /// <para>
    /// <strong>It starts empty on purpose, and the default lives in <see cref="Rungs"/>.</strong>
    /// This is the 8.1 footgun, and this file would have walked straight into it: the configuration
    /// binder does not <em>replace</em> an array property, it binds each configured index into the
    /// array that is already there — so a non-empty default plus a four-element <c>Pace:RungsMs</c>
    /// in appsettings would quietly leave the last four shipped rungs on the end.
    /// </para>
    /// </summary>
    public int[] RungsMs { get; set; } = [];

    /// <summary>The ladder actually in force: what was configured, or the shipped 1s…34s.</summary>
    public int[] Rungs => RungsMs.Length > 0 ? RungsMs : ShippedRungsMs;

    /// <summary>
    /// The fanout exchange for rung <paramref name="index"/> (0-based). Fanout, and one exchange
    /// per rung, for 8.2's reason exactly: the delay queue dead-letters on expiry using the
    /// message's <em>own</em> routing key — which has to still be the original event type or the
    /// message comes back unroutable — so the rung is encoded in the exchange instead.
    /// </summary>
    public string ExchangeFor(int index) => $"artifice.pace.{LabelFor(index)}";

    /// <summary>The delay queue for rung <paramref name="index"/> (0-based).</summary>
    public string QueueFor(int index) => $"artifice.pace.{LabelFor(index)}.queue";

    /// <summary>Human-readable rung name — <c>5s</c>, <c>34s</c>. Part of the queue name, so it must be stable.</summary>
    public string LabelFor(int index)
    {
        var ms = Rungs[index];
        if (ms % 60_000 == 0) { return $"{ms / 60_000}m"; }
        if (ms % 1_000 == 0) { return $"{ms / 1_000}s"; }
        return $"{ms}ms";
    }

    /// <summary>
    /// The rung nearest <paramref name="duration"/>, or <c>null</c> for "don't pace this at all"
    /// — which is what a non-positive duration means, and what makes turning one stage's pacing
    /// off a matter of setting it to zero rather than a second flag.
    /// </summary>
    public int? RungFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return null;
        }

        var wanted = duration.TotalMilliseconds;
        var rungs = Rungs;

        var nearest = 0;
        for (var index = 1; index < rungs.Length; index++)
        {
            if (Math.Abs(rungs[index] - wanted) < Math.Abs(rungs[nearest] - wanted))
            {
                nearest = index;
            }
        }

        return nearest;
    }

    /// <summary>The TTL of rung <paramref name="index"/> — what a caller reports as the delay actually applied.</summary>
    public int MillisecondsFor(int index) => Rungs[index];
}
