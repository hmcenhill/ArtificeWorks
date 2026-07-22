using System.Collections.Concurrent;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Messaging.Outbox;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Hosts the outbox over a real Postgres with a scriptable broker in place of RabbitMQ.
/// <para>
/// The claims 8.1 makes are <em>database</em> claims — the row commits with the work or not at
/// all, <c>SKIP LOCKED</c> means two dispatchers never publish the same row, a failed publish
/// leaves the row for next time — and the only way to race them is to invoke the dispatcher
/// concurrently against real Postgres. A real broker would add nothing here and would make
/// "the broker is down" hard to arrange on purpose; the real publish path is proved end to end
/// in <see cref="WorkerConsumerTests"/>, which now runs through the outbox like everything else.
/// </para>
/// </summary>
public class OutboxFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();

    public ServiceProvider Services { get; private set; } = null!;
    public ScriptableBrokerPublisher Broker { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();

        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        // The real outbox publisher: it writes rows to whatever context the caller is using.
        services.AddScoped<OutboxEventPublisher>();
        services.AddScoped<IEventPublisher>(sp => sp.GetRequiredService<OutboxEventPublisher>());

        services.AddSingleton<IBrokerPublisher>(Broker);
        services.Configure<OutboxConfiguration>(options =>
        {
            options.BatchSize = 10;
            options.InitialBackoffSeconds = 0;
        });

        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
        await context.Database.MigrateAsync();
    }

    /// <summary>A dispatcher wired to this fixture — the real one, invoked a batch at a time.</summary>
    public OutboxDispatcher NewDispatcher() => new(
        Services.GetRequiredService<IServiceScopeFactory>(),
        Services.GetRequiredService<IOptions<OutboxConfiguration>>(),
        Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>>());

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

/// <summary>
/// Stands in for RabbitMQ. Records what was published, in order, and can be told to fail —
/// which is how "the broker is down" becomes a thing a test can arrange rather than a thing it
/// has to wait for.
/// </summary>
public sealed class ScriptableBrokerPublisher : IBrokerPublisher
{
    private readonly ConcurrentQueue<(string RoutingKey, Guid EventId, Guid CorrelationId, string Payload)> _published = new();

    /// <summary>When true, every publish throws — the broker outage.</summary>
    public bool IsDown { get; set; }

    public IReadOnlyList<(string RoutingKey, Guid EventId, Guid CorrelationId, string Payload)> Published => _published.ToList();

    public Task PublishRawAsync(
        string routingKey,
        string payload,
        Guid eventId,
        Guid correlationId,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        if (IsDown)
        {
            throw new InvalidOperationException("Broker is unreachable.");
        }

        _published.Enqueue((routingKey, eventId, correlationId, payload));
        return Task.CompletedTask;
    }
}
