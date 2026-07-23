using System.Diagnostics;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// The last mile: put these bytes on the exchange under this routing key.
/// <para>
/// Since 8.1 this is <em>not</em> what application code uses — handlers publish through
/// <c>IEventPublisher</c>, which writes an outbox row. The only caller of this interface is the
/// <see cref="Outbox.OutboxDispatcher"/>, which is exactly the point: there is one place in the
/// system that talks to the broker, and it is the one that can retry.
/// </para>
/// </summary>
public interface IBrokerPublisher
{
    /// <param name="headers">Optional AMQP headers — 8.2's retry ladder carries its attempt count here.</param>
    /// <param name="parentContext">
    /// The trace to publish under (9.1). The dispatcher restores it from the outbox row, because it
    /// runs on a background thread with no ambient activity — without it every trace in the system
    /// would end at the commit that staged the row. Null falls back to <c>Activity.Current</c>.
    /// </param>
    Task PublishRawAsync(
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        ActivityContext? parentContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes to a named exchange rather than the shared one — which since 10.1 the dispatcher
    /// needs too, to drop a paced event onto a delay rung instead of straight onto
    /// <c>artifice.events</c>.
    /// </summary>
    /// <param name="pacedMs">
    /// How long this message will rest in a delay queue before delivery, or null when it goes
    /// straight through. Stamped on the producer span as <c>artificeworks.paced_ms</c> so the
    /// seconds-wide gap it opens in the Tempo waterfall reads as <em>explained</em> rather than as
    /// a stall — a paced trace is supposed to have a hole in it (10.1).
    /// </param>
    Task PublishToAsync(
        string exchange,
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        ActivityContext? parentContext = null,
        int? pacedMs = null,
        CancellationToken cancellationToken = default);
}
