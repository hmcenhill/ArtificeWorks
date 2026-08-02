using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Application.SubAssemblies;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Hosts the <em>whole</em> pipeline over a real Postgres, with no broker — picking, production,
/// inspection, shipping, putaway and the sub-assembly loop, which is more than any earlier fixture
/// wires because 13.3 is the first story whose subject spans all of them.
/// <para>
/// The same split every epic since 5.3 has taken: the guarantees under test are <em>database</em>
/// properties — a filtered unique index that admits one live request, a stock credit committed with
/// a terminal transition, a DELETE that must not orphan a running child — and the only way to race
/// them is to invoke the workflows concurrently. Through RabbitMQ the consumer would serialize
/// deliveries at prefetch 1 and the contention would never happen. The broker path has its own
/// end-to-end test (<see cref="WorkerConsumerTests"/>).
/// </para>
/// <para>
/// The world sweep is wired here too, and that is not padding: 10.4's retire is the sharpest edge in
/// this story, and "does the sweep leave a parent with a live child alone?" is only answerable
/// against a real DELETE and a real foreign key.
/// </para>
/// </summary>
public class SubAssemblyFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();

    public ServiceProvider Services { get; private set; } = null!;
    public RecordingEventPublisher Published { get; } = new();
    public ScriptableVerdictSource Verdicts { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<PipelineSnapshotCache>();
        services.AddSingleton<ArtificeWorksMetrics>();
        services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));

        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        services.AddScoped<IProductionRunRepository, ProductionRunRepository>();
        services.AddScoped<IInspectionRunRepository, InspectionRunRepository>();
        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<IComponentStockRepository, ComponentStockRepository>();
        services.AddScoped<IWorldRepository, WorldRepository>();

        services.AddScoped<MaterialPickingService>();
        services.AddScoped<ProductionService>();
        services.AddScoped<InspectionService>();
        services.AddScoped<ShippingService>();
        services.AddScoped<SubAssemblyService>();
        services.AddScoped<StockPutawayService>();

        services.AddSingleton<IEventPublisher>(Published);
        services.AddSingleton<IVerdictSource>(Verdicts);
        services.AddSingleton(new InspectionConfiguration());
        services.AddSingleton(new ProductionConfiguration());
        services.AddSingleton(new ShippingConfiguration());
        services.AddSingleton<ICarrierBooking, ConfiguredCarrierBooking>();
        services.AddSingleton<SimulationSettingsCache>();

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
