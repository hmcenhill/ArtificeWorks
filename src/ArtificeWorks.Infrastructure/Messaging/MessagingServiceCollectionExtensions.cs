using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Messaging.Outbox;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers only the shared RabbitMQ connection (singleton) and binds
    /// <see cref="RabbitMqConfiguration"/>. This is all a consume-only host needs.
    /// </summary>
    public static IServiceCollection AddRabbitMqConnection(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqConfiguration>(configuration.GetSection(nameof(RabbitMqConfiguration)));
        services.Configure<RetryConfiguration>(configuration.GetSection(RetryConfiguration.SectionName));

        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();

        // Singleton, and stateless by design: the outbox dispatcher and the consumer's retry
        // ladder are both background loops with no ambient request to take a correlation id from,
        // so the thing that touches the wire must not need one.
        services.AddSingleton<RabbitMqRawPublisher>();
        services.AddSingleton<IBrokerPublisher>(sp => sp.GetRequiredService<RabbitMqRawPublisher>());

        return services;
    }

    /// <summary>
    /// Registers full messaging for a publishing host.
    /// <para>
    /// <strong>Note what <c>IEventPublisher</c> resolves to since 8.1.</strong> Application code
    /// gets the <see cref="OutboxEventPublisher"/>, which writes a row in the caller's
    /// transaction; the <see cref="RabbitMqEventPublisher"/> is still here but registered as
    /// <see cref="IBrokerPublisher"/>, and the only thing that resolves it is the
    /// <see cref="OutboxDispatcher"/>. One place in the system talks to the broker, and it is the
    /// one that can retry.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRabbitMqConnection(configuration);

        services.Configure<OutboxConfiguration>(configuration.GetSection(OutboxConfiguration.SectionName));

        services.AddScoped<RabbitMqEventPublisher>();

        services.AddScoped<OutboxEventPublisher>();
        services.AddScoped<IEventPublisher>(sp => sp.GetRequiredService<OutboxEventPublisher>());
        services.AddScoped<IRawEventPublisher>(sp => sp.GetRequiredService<OutboxEventPublisher>());

        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        return services;
    }

    /// <summary>
    /// Adds the two background loops that keep the outbox honest: the dispatcher that drains it
    /// to the broker, and the sweep that ages out finished bookkeeping. Both hosts run both —
    /// the API writes outbox rows too, because it is where the pipeline starts.
    /// </summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<RetentionSweepService>();
        return services;
    }
}
