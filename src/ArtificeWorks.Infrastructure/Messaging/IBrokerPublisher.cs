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
}
