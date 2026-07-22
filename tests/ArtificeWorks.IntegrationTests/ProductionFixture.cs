using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Hosts the production and inspection workflows over a real Postgres, with no broker.
/// <para>
/// Same split Epic 5 arrived at (5.3) and 6.4 deliberately reuses: the guarantees this epic is
/// about — an attempt builds once, an attempt is inspected once — are <em>database</em>
/// properties, and the only way to race them is to invoke the workflow concurrently. Driving
/// them through RabbitMQ would prove the opposite of what's wanted, because the consumer runs
/// prefetch 1 on a single channel and serializes deliveries. The broker path gets its own
/// end-to-end test (<see cref="WorkerConsumerTests"/>).
/// </para>
/// </summary>
public class ProductionFixture : IAsyncLifetime
{
    /// <summary>Matches the shipped default, so the cap tests assert the real configuration.</summary>
    public const int MaxRebuildAttempts = 3;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();

    public ServiceProvider Services { get; private set; } = null!;
    public RecordingEventPublisher Published { get; } = new();
    public ScriptableVerdictSource Verdicts { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        // The workflow services record metrics since 9.2; the real instruments cost nothing when
        // nothing is listening, and a test that wants to assert on a count attaches a
        // MetricCollector to this same meter factory rather than to a stub.
        services.AddMetrics();
        services.AddSingleton<PipelineSnapshotCache>();
        services.AddSingleton<ArtificeWorksMetrics>();
        services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        services.AddScoped<IProductionRunRepository, ProductionRunRepository>();
        services.AddScoped<IInspectionRunRepository, InspectionRunRepository>();
        services.AddScoped<MaterialPickingService>();
        services.AddScoped<ProductionService>();
        services.AddScoped<InspectionService>();
        services.AddSingleton<IEventPublisher>(Published);

        // The real RandomVerdictSource is a coin flip; these tests need to decide outcomes, so
        // they swap in a scripted one at exactly the seam Epic 10 and Epic 12 will use.
        services.AddSingleton<IVerdictSource>(Verdicts);
        services.AddSingleton(new InspectionConfiguration());
        services.AddSingleton(new ProductionConfiguration { MaxRebuildAttempts = MaxRebuildAttempts });

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

/// <summary>
/// A verdict source the test drives. Everything passes unless a test says otherwise, which
/// keeps the happy-path tests honest about the shipped default.
/// </summary>
public sealed class ScriptableVerdictSource : IVerdictSource
{
    private readonly Lock _gate = new();
    private int _failuresRemaining;
    private bool _failEverything;

    /// <summary>Fails the next <paramref name="count"/> units judged, then goes back to passing.</summary>
    public void FailNext(int count)
    {
        lock (_gate)
        {
            _failuresRemaining = count;
            _failEverything = false;
        }
    }

    public void FailEverything()
    {
        lock (_gate)
        {
            _failEverything = true;
            _failuresRemaining = 0;
        }
    }

    public void PassEverything()
    {
        lock (_gate)
        {
            _failEverything = false;
            _failuresRemaining = 0;
        }
    }

    public UnitVerdict Verdict(StockKeepingUnit unit)
    {
        lock (_gate)
        {
            if (_failEverything || _failuresRemaining > 0)
            {
                _failuresRemaining--;
                return new UnitVerdict(false, "cracked mainspring");
            }
            return new UnitVerdict(true);
        }
    }
}
