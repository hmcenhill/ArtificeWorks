namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// One entry in the dispatch table: the wire <see cref="EventType"/> (RabbitMQ routing
/// key) paired with a closure that knows how to deserialize that event's envelope and
/// invoke its typed handler. One is registered per <c>AddEventHandler</c> call, and the
/// <see cref="EventDispatcher"/> collects them all from DI — so the dispatcher stays
/// oblivious to concrete event/handler types.
/// </summary>
/// <param name="EventType">The event's stable type string, used as the routing key.</param>
/// <param name="Dispatch">Deserializes the body into the event's envelope and runs its handler,
/// resolving the handler from the supplied per-message scope.</param>
public sealed record EventRegistration(
    string EventType,
    Func<IServiceProvider, ReadOnlyMemory<byte>, CancellationToken, Task> Dispatch);
