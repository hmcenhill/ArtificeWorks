namespace ArtificeWorks.Infrastructure.Messaging.Outbox;

/// <summary>
/// Knobs for the outbox dispatcher and the retention sweep, bound from the <c>Outbox</c>
/// configuration section. The shipped defaults are chosen for a demo: a poll fast enough that
/// nobody watching notices the hop, and a retention long enough that a visitor can still see
/// yesterday's events.
/// </summary>
public sealed class OutboxConfiguration
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// How often the dispatcher looks for unsent rows. ~1s is invisible in a pipeline whose
    /// stages already take longer than that, and a poller is restart-safe by construction —
    /// which a "publish right after commit, poll as a fallback" scheme is not, because the fast
    /// path is exactly the one that skips when the process dies.
    /// </summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>Rows claimed per pass. Small, because the claim holds a transaction open across publishes.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>First backoff after a failed publish; doubles per attempt up to <see cref="MaxBackoffSeconds"/>.</summary>
    public int InitialBackoffSeconds { get; set; } = 2;

    public int MaxBackoffSeconds { get; set; } = 60;

    /// <summary>
    /// How long a sent row is kept as evidence. Mark-sent rather than delete-on-send, because
    /// Epic 11 will want it and 7.4's timeline was shaped to merge an <c>event</c> kind later.
    /// </summary>
    public int SentRetentionHours { get; set; } = 72;

    /// <summary>How long an API idempotency key (8.4) is honoured before the sweep removes it.</summary>
    public int IdempotencyKeyRetentionHours { get; set; } = 72;

    /// <summary>How long a replayed dead letter (8.3) is kept before the sweep removes it.</summary>
    public int DeadLetterRetentionHours { get; set; } = 720;

    /// <summary>How often the retention sweep runs.</summary>
    public int SweepIntervalMinutes { get; set; } = 30;
}
