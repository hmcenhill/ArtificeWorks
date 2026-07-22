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

    public OutboxMessage(
        Guid eventId,
        string eventType,
        Guid correlationId,
        string payload,
        DateTime occurredUtc,
        string? traceParent = null,
        string? traceState = null)
    {
        EventId = eventId;
        EventType = eventType;
        CorrelationId = correlationId;
        Payload = payload;
        OccurredUtc = occurredUtc;
        TraceParent = traceParent;
        TraceState = traceState;
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

    /// <summary>
    /// The W3C <c>traceparent</c> of the activity this row was staged inside (9.1), captured for
    /// exactly the same reason <see cref="CorrelationId"/> is.
    /// <para>
    /// <strong>Without this, every trace in the system ends at a commit.</strong> 8.1 moved
    /// publishing from after the transaction to staged inside it, so the event goes on the wire up
    /// to a second later, on a background thread, with no ambient activity at all — and a default
    /// OpenTelemetry setup would happily start a fresh, parentless one-span trace for each publish.
    /// Fully instrumented, entirely disconnected, and it looks correct right up until you try to
    /// follow an order across a stage boundary. Captured at stage time, restored at publish time.
    /// </para>
    /// <para>
    /// <strong>A column, not a field in the payload.</strong> The payload is the domain event and
    /// 8.3 replays it verbatim; transport metadata inside it would mean a replayed message carries
    /// a stale, long-closed trace. Columns are also queryable, which 9.4's runbook needs.
    /// </para>
    /// <para>Nullable: a row staged with no ambient activity publishes untraced, never broken.</para>
    /// </summary>
    public string? TraceParent { get; private set; }

    /// <summary>Vendor trace state travelling with <see cref="TraceParent"/>. Almost always null here.</summary>
    public string? TraceState { get; private set; }

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
