using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ArtificeWorks.Api.Realtime;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Infrastructure.Messaging.Outbox;
using ArtificeWorks.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

public class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private WebApplicationFactory<Program> _factory;
    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// The API's own container, so a test can arrange state the API has no endpoint for —
    /// notably running production, which is only ever triggered by an event.
    /// </summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>
    /// The carrier seam, swapped for one a test can drive. It delegates to the real booking
    /// source unless a test scripts a refusal, so the happy-path tests still exercise the
    /// shipped default (<c>RefusalRate</c> 0.0) rather than a stub of it.
    /// </summary>
    public ScriptableCarrierBooking Carriers { get; } = new();

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

                    // Since 8.1 the publisher application code gets writes an outbox row rather
                    // than touching the broker, so these tests keep the REAL publisher and can
                    // assert on `outbox_messages` — the point of the story is that the event is
                    // part of the same transaction as the work, and a no-op here would hide
                    // exactly that. What does need a broker is the dispatcher, so its two hosted
                    // services come out instead — along with the dashboard relay (11.2), which
                    // consumes from the broker and has no bearing on these API-surface tests.
                    foreach (var hosted in services
                                 .Where(d => d.ServiceType == typeof(IHostedService)
                                     && (d.ImplementationType == typeof(OutboxDispatcher)
                                         || d.ImplementationType == typeof(RetentionSweepService)
                                         || d.ImplementationType == typeof(DashboardRelay)))
                                 .ToList())
                    {
                        services.Remove(hosted);
                    }

                    var carrierRegistration = services.SingleOrDefault(d => d.ServiceType == typeof(ICarrierBooking));
                    if (carrierRegistration is not null)
                    {
                        services.Remove(carrierRegistration);
                    }
                    services.AddSingleton<ICarrierBooking>(Carriers);
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
