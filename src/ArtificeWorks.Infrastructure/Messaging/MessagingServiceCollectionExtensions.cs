using ArtificeWorks.Application.Messaging;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers RabbitMQ messaging: the shared connection (singleton), the event
    /// publisher (scoped, since it reads the per-request correlation id), and a default
    /// scoped <see cref="CorrelationContext"/>. Hosts that establish correlation
    /// differently (e.g. the worker, from a consumed envelope) can override the
    /// registration after calling this.
    /// </summary>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqConfiguration>(configuration.GetSection(nameof(RabbitMqConfiguration)));

        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();

        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        return services;
    }
}
