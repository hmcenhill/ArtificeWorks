using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// Routes a consumed message to its handler by event type. Builds its dispatch table from
/// every <see cref="EventRegistration"/> in DI, so it never references a concrete event or
/// handler. Each dispatch runs in its own DI scope, giving handlers a fresh scoped
/// <c>DbContext</c> and repository per message.
/// </summary>
public sealed class EventDispatcher
{
    private readonly IReadOnlyDictionary<string, EventRegistration> _registrations;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventDispatcher> _logger;

    public EventDispatcher(
        IEnumerable<EventRegistration> registrations,
        IServiceScopeFactory scopeFactory,
        ILogger<EventDispatcher> logger)
    {
        _registrations = registrations.ToDictionary(r => r.EventType);
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The event types that have a handler — the exact set of routing keys to bind.</summary>
    public IReadOnlyCollection<string> HandledEventTypes => (IReadOnlyCollection<string>)_registrations.Keys;

    /// <summary>
    /// Deserializes and dispatches one message. Throws if the handler throws, so the
    /// consumer can decide the ack/nack outcome. An unrecognised event type is logged and
    /// treated as a no-op (the queue only binds handled keys, so this is defensive).
    /// </summary>
    public async Task DispatchAsync(string eventType, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!_registrations.TryGetValue(eventType, out var registration))
        {
            _logger.LogWarning("No handler registered for event type {EventType}; message ignored.", eventType);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        await registration.Dispatch(scope.ServiceProvider, body, cancellationToken);
    }
}
