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
    Task PublishRawAsync(
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default);
}
