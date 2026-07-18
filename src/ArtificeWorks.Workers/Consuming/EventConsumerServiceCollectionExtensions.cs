using System.Runtime.CompilerServices;
using System.Text.Json;

using ArtificeWorks.Application.Messaging;

using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.Workers.Consuming;

public static class EventConsumerServiceCollectionExtensions
{
    // Must match the publisher's serialization (RabbitMqEventPublisher uses Web defaults)
    // so the envelope round-trips.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers the shared consumption plumbing: the dispatch table's <see cref="EventDispatcher"/>
    /// and the hosted <see cref="RabbitMqConsumerService"/>. Call once; then register handlers
    /// with <see cref="AddEventHandler{TEvent,THandler}"/>.
    /// </summary>
    public static IServiceCollection AddEventConsumer(this IServiceCollection services)
    {
        services.AddSingleton<EventDispatcher>();
        services.AddHostedService<RabbitMqConsumerService>();
        return services;
    }

    /// <summary>
    /// Registers a handler for one event type. This is the <em>only</em> change needed to
    /// consume a new event: the queue binding, dispatch, and ack/nack plumbing pick it up
    /// automatically. Registers the handler (scoped, one per message) and an
    /// <see cref="EventRegistration"/> the dispatcher collects.
    /// </summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();

        services.AddSingleton(new EventRegistration(
            EventType: EventTypeOf<TEvent>(),
            Dispatch: async (provider, body, cancellationToken) =>
            {
                var envelope = JsonSerializer.Deserialize<EventEnvelope<TEvent>>(body.Span, SerializerOptions)
                    ?? throw new InvalidOperationException(
                        $"Envelope for {typeof(TEvent).Name} deserialized to null.");

                var handler = provider.GetRequiredService<IIntegrationEventHandler<TEvent>>();
                await handler.HandleAsync(envelope, cancellationToken);
            }));

        return services;
    }

    /// <summary>
    /// Reads an event's routing key from its own <see cref="IntegrationEvent.EventType"/>.
    /// That property is a compile-time constant on each event record, so we read it once
    /// from an uninitialized instance (no constructor args required) rather than
    /// duplicating the wire string here — the contract stays defined in exactly one place.
    /// </summary>
    private static string EventTypeOf<TEvent>() where TEvent : IntegrationEvent
        => ((TEvent)RuntimeHelpers.GetUninitializedObject(typeof(TEvent))).EventType;
}
