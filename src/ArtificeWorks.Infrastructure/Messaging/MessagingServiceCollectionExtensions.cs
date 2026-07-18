using ArtificeWorks.Application.Messaging;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers only the shared RabbitMQ connection (singleton) and binds
    /// <see cref="RabbitMqConfiguration"/>. This is all a consume-only host needs — the
    /// Workers service uses it directly, since it has no reason to register a publisher
    /// or a per-request correlation context.
    /// </summary>
    public static IServiceCollection AddRabbitMqConnection(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqConfiguration>(configuration.GetSection(nameof(RabbitMqConfiguration)));
        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        return services;
    }

    /// <summary>
    /// Registers full RabbitMQ messaging for a publishing host: the shared connection, the
    /// event publisher (scoped, since it reads the per-request correlation id), and a
    /// default scoped <see cref="CorrelationContext"/>. Hosts that establish correlation
    /// differently can override the registration after calling this.
    /// </summary>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRabbitMqConnection(configuration);

        services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();

        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        return services;
    }
}
