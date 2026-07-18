using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

public class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private WebApplicationFactory<Program> _factory;
    public HttpClient Client { get; private set; } = null!;

    public ApiFixture()
    {
        _container = new PostgreSqlBuilder("postgres:15.1").Build();
        _factory = new WebApplicationFactory<Program>();

    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = _factory.WithWebHostBuilder(host =>
            {
                host.ConfigureServices(services =>
                {
                    var existingRegistration = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ArtificeWorksDbContext>));
                    if (existingRegistration is not null)
                    {
                        services.Remove(existingRegistration);
                    }

                    services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(_container.GetConnectionString()));

                    // These tests assert HTTP + persistence, not messaging: replace the
                    // RabbitMQ publisher with a no-op so no broker is required (4.2 covers
                    // the real publish→consume path).
                    var publisherRegistration = services.SingleOrDefault(d => d.ServiceType == typeof(IEventPublisher));
                    if (publisherRegistration is not null)
                    {
                        services.Remove(publisherRegistration);
                    }
                    services.AddScoped<IEventPublisher, NoOpEventPublisher>();
                });
            });

        Client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        await _factory.DisposeAsync();
    }
}
