namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// Publishes an event whose envelope is <em>already serialized</em> — the shape replay needs
/// (8.3), where the payload is bytes recovered from a dead letter rather than a typed event
/// anyone still has an object for.
/// <para>
/// Separate from <see cref="IEventPublisher"/> on purpose. Ordinary code should publish typed
/// events and never think about wire format; this is the one deliberate back door, and keeping
/// it a distinct interface means a caller has to ask for it by name.
/// </para>
/// <para>
/// Like <see cref="IEventPublisher"/>, the implementation writes to the outbox — so replay, the
/// one endpoint whose entire job is "reliably re-send this", is not the one place that publishes
/// unreliably.
/// </para>
/// </summary>
public interface IRawEventPublisher
{
    /// <param name="eventType">The routing key the message goes back out under.</param>
    /// <param name="payload">The serialized <c>EventEnvelope&lt;T&gt;</c>, forwarded verbatim.</param>
    /// <param name="correlationId">Carried through so a replayed message still greps as one story.</param>
    Task EnqueueAsync(string eventType, string payload, Guid correlationId, CancellationToken cancellationToken = default);
}
