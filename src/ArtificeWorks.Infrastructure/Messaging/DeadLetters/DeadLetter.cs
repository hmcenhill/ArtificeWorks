namespace ArtificeWorks.Infrastructure.Messaging.DeadLetters;

/// <summary>
/// A message the pipeline could not handle, drained out of <c>artifice.parked</c> and turned into
/// something a human can read (8.3).
/// <para>
/// <strong>A table, not a queue to browse.</strong> Browsing AMQP from a request handler is
/// awkward, semi-destructive and gives up the moment someone purges the queue. A row is an
/// ordinary query, joins to the work order, survives a broker restart, and is what Epic 11 can
/// render without putting AMQP in a browser.
/// </para>
/// <para>
/// Every field here exists so the failure can be diagnosed <em>without</em> opening the RabbitMQ
/// management UI: what event, which order, how many attempts, what error.
/// </para>
/// </summary>
public class DeadLetter
{
    private DeadLetter()
    {
        EventType = null!;
        Payload = null!;
        LastError = null!;
    }

    public DeadLetter(
        string eventType,
        string payload,
        Guid correlationId,
        Guid? workOrderId,
        int attempts,
        string lastError,
        DateTime parkedUtc)
    {
        Id = Guid.NewGuid();
        EventType = eventType;
        Payload = payload;
        CorrelationId = correlationId;
        WorkOrderId = workOrderId;
        Attempts = attempts;
        LastError = lastError;
        ParkedUtc = parkedUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The original routing key — what the message goes back out under on replay.</summary>
    public string EventType { get; private set; }

    /// <summary>
    /// The original envelope, verbatim. Text rather than a parsed shape on purpose: this table
    /// exists to hold messages that are already known to be broken, and one that won't parse
    /// still has to end up here.
    /// </summary>
    public string Payload { get; private set; }

    public Guid CorrelationId { get; private set; }

    /// <summary>
    /// Lifted out of the payload where it is present, so a failure can be shown next to the order
    /// it belongs to. Null for a body that wouldn't parse — which is a fact worth having, not an
    /// error.
    /// </summary>
    public Guid? WorkOrderId { get; private set; }

    public int Attempts { get; private set; }

    public string LastError { get; private set; }

    public DateTime ParkedUtc { get; private set; }

    public DateTime? ReplayedUtc { get; private set; }

    public int ReplayCount { get; private set; }

    public void MarkReplayed(DateTime replayedUtc)
    {
        ReplayedUtc = replayedUtc;
        ReplayCount++;
    }
}
