using System.Collections.Concurrent;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Hosts the picking workflow over a real Postgres, with no broker.
/// <para>
/// The reservation guarantees this epic is about — all-or-nothing, no double-allocation,
/// idempotency — are all <em>database</em> properties, and the only way to actually race them
/// is to invoke the workflow concurrently. Driving these through RabbitMQ would prove the
/// opposite of what's wanted: the consumer runs with prefetch 1 on a single channel, so
/// deliveries would be handled one at a time and the contention under test would never happen.
/// The broker path gets its own end-to-end test (<see cref="WorkerConsumerTests"/>).
/// </para>
/// </summary>
public class MaterialPickingFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();

    public ServiceProvider Services { get; private set; } = null!;
    public RecordingEventPublisher Published { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        services.AddScoped<MaterialPickingService>();
        services.AddSingleton<IEventPublisher>(Published);

        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
        await context.Database.MigrateAsync();
    }

    /// <summary>A context outside any workflow scope, for arranging and asserting.</summary>
    public ArtificeWorksDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ArtificeWorksDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>Captures published events so tests can assert the pipeline hand-off.</summary>
public sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly ConcurrentQueue<IntegrationEvent> _events = new();

    public IReadOnlyList<T> OfType<T>() where T : IntegrationEvent => _events.OfType<T>().ToList();

    public Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default)
        where T : IntegrationEvent
    {
        _events.Enqueue(@event);
        return Task.CompletedTask;
    }
}
