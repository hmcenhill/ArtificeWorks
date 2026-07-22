namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// The retry ladder (8.2), bound from the <c>Retry</c> configuration section.
/// <para>
/// <strong>Broker-native delay queues, not in-process backoff.</strong> With prefetch 1, sleeping
/// inside a handler holds the un-acked message and stalls the entire pipeline for the length of
/// the backoff — and a worker restart loses the retry outright. TTL'd queues survive restarts,
/// cost nothing while waiting, and are visible in the RabbitMQ management UI, which both Epic 11
/// and Epic 12 want to point at.
/// </para>
/// <para>
/// <strong>A fixed ladder, not computed per-message delays.</strong> Per-message TTL on a shared
/// queue does not expire out of order — RabbitMQ only ever checks the message at the head — which
/// is the classic trap here. Fixed-TTL queues sidestep it entirely and are trivial to draw.
/// </para>
/// </summary>
public sealed class RetryConfiguration
{
    public const string SectionName = "Retry";

    /// <summary>The queue that holds messages nobody could handle. No TTL, and no consumer but 8.3's drain.</summary>
    public const string ParkedQueueName = "artifice.parked";

    /// <summary>The ladder used when configuration does not name one.</summary>
    private static readonly int[] ShippedDelaysMs = [5_000, 30_000, 120_000];

    /// <summary>
    /// One rung per delay, in milliseconds, as configured.
    /// <para>
    /// <strong>It starts empty on purpose, and the default lives in <see cref="Delays"/>.</strong>
    /// The configuration binder does not <em>replace</em> an array property, it binds each
    /// configured index into the array that is already there — so a non-empty default plus a
    /// three-element <c>Retry:DelaysMs</c> in appsettings would quietly produce a six-rung ladder
    /// with the defaults still on the front. Starting empty makes configuring the ladder mean
    /// what it looks like it means.
    /// </para>
    /// </summary>
    public int[] DelaysMs { get; set; } = [];

    /// <summary>The ladder actually in force: what was configured, or the shipped 5s / 30s / 2m.</summary>
    public int[] Delays => DelaysMs.Length > 0 ? DelaysMs : ShippedDelaysMs;

    /// <summary>
    /// The fanout exchange for rung <paramref name="index"/> (0-based).
    /// <para>
    /// Fanout, and one exchange per rung, for a reason worth writing down: a delay queue
    /// dead-letters on expiry to <c>artifice.events</c> using the message's <em>own</em> routing
    /// key, and that key has to still be the original event type or the message comes back
    /// unroutable. A single direct retry exchange would have to consume the routing key to
    /// select the rung, so the rung is encoded in the exchange instead and the routing key rides
    /// through untouched.
    /// </para>
    /// </summary>
    public string ExchangeFor(int index) => $"artifice.retry.{LabelFor(index)}";

    /// <summary>The delay queue for rung <paramref name="index"/> (0-based).</summary>
    public string QueueFor(int index) => $"artifice.retry.{LabelFor(index)}.queue";

    /// <summary>Human-readable rung name — <c>5s</c>, <c>30s</c>, <c>2m</c>. Part of the queue name, so it must be stable.</summary>
    public string LabelFor(int index)
    {
        var ms = Delays[index];
        if (ms % 60_000 == 0) { return $"{ms / 60_000}m"; }
        if (ms % 1_000 == 0) { return $"{ms / 1_000}s"; }
        return $"{ms}ms";
    }

    /// <summary>How many retries the ladder offers before a message parks.</summary>
    public int MaxAttempts => Delays.Length;
}
