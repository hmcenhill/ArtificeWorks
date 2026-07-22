using ArtificeWorks.Application.Handlers;
using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Hosts the whole pipeline over a real Postgres, with no broker — the same split Epics 5 and 6
/// arrived at. Epic 7's guarantees (a shipment is booked once per order, a parcel is dispatched
/// once) are database and state-machine properties, and the only way to race them is to invoke
/// the workflow concurrently; the prefetch-1 consumer would serialize them away. The broker path
/// gets its own end-to-end test (<see cref="WorkerConsumerTests"/>).
/// </summary>
public class ShippingFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:15.1").Build();

    public ServiceProvider Services { get; private set; } = null!;
    public RecordingEventPublisher Published { get; } = new();
    public ScriptableVerdictSource Verdicts { get; } = new();
    public ScriptableCarrierBooking Carriers { get; } = new();

    /// <summary>
    /// The live configuration singleton, so a test can flip <c>AutoBook</c> the way an operator
    /// would. Tests in a class run sequentially and each resets it, so this stays honest.
    /// </summary>
    public ShippingConfiguration ShippingConfig { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ArtificeWorksDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
        services.AddScoped<IProductionRunRepository, ProductionRunRepository>();
        services.AddScoped<IInspectionRunRepository, InspectionRunRepository>();
        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<IWorkOrderTimelineRepository, WorkOrderTimelineRepository>();
        services.AddScoped<MaterialPickingService>();
        services.AddScoped<ProductionService>();
        services.AddScoped<InspectionService>();
        services.AddScoped<ShippingService>();
        services.AddSingleton<IEventPublisher>(Published);

        // Both random sources are swapped for scripted ones at exactly the seams Epic 10 and
        // Epic 12 will use — a coin flip can't be asserted on, and these tests decide outcomes.
        services.AddSingleton<IVerdictSource>(Verdicts);
        services.AddSingleton<ICarrierBooking>(Carriers);
        services.AddSingleton(new InspectionConfiguration());
        services.AddSingleton(new ProductionConfiguration());
        services.AddSingleton(ShippingConfig);

        // The release re-trigger (7.3) lives on the API's handler, not on the shipping service,
        // so proving it needs the handler here too.
        services.AddScoped<WorkOrderHandler>();

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
/// The real booking source with a switch on it. Carriers accept unless a test says otherwise —
/// which keeps the happy-path tests honest about the shipped default (<c>RefusalRate</c> 0.0) —
/// and an unknown carrier is still reported as an unknown carrier, because that is the caller's
/// mistake and outranks a scripted refusal.
/// </summary>
public sealed class ScriptableCarrierBooking : ICarrierBooking
{
    private readonly ICarrierBooking _real = new ConfiguredCarrierBooking(new ShippingConfiguration());
    private readonly Lock _gate = new();
    private string? _refusalReason;

    public void RefuseEverything(string reason = "No carrier capacity available.")
    {
        lock (_gate) { _refusalReason = reason; }
    }

    public void AcceptEverything()
    {
        lock (_gate) { _refusalReason = null; }
    }

    public CarrierBookingResult Book(CarrierBookingRequest request)
    {
        var result = _real.Book(request);
        if (result.Outcome == CarrierBookingOutcome.UnknownCarrier)
        {
            return result;
        }

        lock (_gate)
        {
            return _refusalReason is { } reason
                ? CarrierBookingResult.Refused(result.Carrier!, reason)
                : result;
        }
    }
}
