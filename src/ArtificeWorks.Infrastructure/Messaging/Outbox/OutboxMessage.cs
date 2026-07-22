namespace ArtificeWorks.Infrastructure.Messaging.Outbox;

/// <summary>
/// One event, durably recorded for publication in the <em>same transaction</em> as the work that
/// caused it. The row closing the dual-write gap that had been open since 4.1 (8.1).
/// <para>
/// <strong>Why this lives in Infrastructure, not Domain.</strong> Every other entity in this
/// system is something the factory has an opinion about — an order, a parcel, a reservation. An
/// outbox row is a fact about the delivery mechanism, meaningless to the domain, and the domain
/// project deliberately depends on nothing. It is persisted, but it is plumbing.
/// </para>
/// <para>
/// <strong>The envelope is serialized at write time.</strong> <see cref="Payload"/> is the
/// complete <c>EventEnvelope&lt;T&gt;</c> JSON, correlation id and all, captured where the work
/// happened. The dispatcher is a background loop with no request and no delivery behind it — if
/// it stamped the correlation id itself it would invent one, and 4.3's thread would end at the
/// outbox.
/// </para>
/// </summary>
public class OutboxMessage
{
    // EF materialises through this; the parameterless ctor stays private so callers use the real one.
    private OutboxMessage()
    {
        EventType = null!;
        Payload = null!;
    }

    public OutboxMessage(Guid eventId, string eventType, Guid correlationId, string payload, DateTime occurredUtc)
    {
        EventId = eventId;
        EventType = eventType;
        CorrelationId = correlationId;
        Payload = payload;
        OccurredUtc = occurredUtc;
    }

    /// <summary>
    /// Store-generated and monotonic, which is the whole reason it is a <c>bigint</c> identity and
    /// not the envelope's Guid: the dispatcher claims and publishes in id order, so events leave in
    /// the order they were written.
    /// </summary>
    public long Id { get; private set; }

    /// <summary>The envelope's event id — carried onto the AMQP <c>message_id</c> on publish.</summary>
    public Guid EventId { get; private set; }

    /// <summary>The event type, which is also the routing key.</summary>
    public string EventType { get; private set; }

    /// <summary>Captured at write time so a background dispatcher never has to invent one.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>The serialized <c>EventEnvelope&lt;T&gt;</c>, published verbatim.</summary>
    public string Payload { get; private set; }

    public DateTime OccurredUtc { get; private set; }

    /// <summary>Null until the dispatcher has published it. Rows are marked, not deleted — see the sweep.</summary>
    public DateTime? SentUtc { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// When a failed row becomes eligible again. Null means "now". This is what keeps a broker
    /// outage from spinning the dispatcher at full speed against a closed door.
    /// </summary>
    public DateTime? NextAttemptUtc { get; private set; }

    public void MarkSent(DateTime sentUtc)
    {
        SentUtc = sentUtc;
        Attempts++;
        LastError = null;
        NextAttemptUtc = null;
    }

    /// <summary>
    /// Records a failed publish and backs the row off. The row stays <em>unsent</em> deliberately:
    /// a broker outage delays events, it never loses them, and the backlog drains when the broker
    /// returns. That is the resilience the old swallow-and-log was protecting, kept — minus the
    /// part where the message disappeared.
    /// </summary>
    public void RecordFailure(string error, DateTime nextAttemptUtc)
    {
        Attempts++;
        LastError = Truncate(error);
        NextAttemptUtc = nextAttemptUtc;
    }

    private static string Truncate(string error) => error.Length <= 1000 ? error : error[..997] + "...";
}
