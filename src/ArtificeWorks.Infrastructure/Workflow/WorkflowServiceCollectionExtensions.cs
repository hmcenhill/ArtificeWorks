using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Recovery;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Application.SubAssemblies;
using ArtificeWorks.Infrastructure.Data;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.Infrastructure.Workflow;

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Epic 6 production and inspection workflow: its repositories, its
    /// configuration, the verdict source, and the two services.
    /// <para>
    /// Both hosts need all of it. The worker runs the stages; the API needs the inspection
    /// service because the manual verdict endpoint drives the <em>same</em> workflow — that is
    /// the point of the endpoint, not a convenience.
    /// </para>
    /// <para>
    /// Configuration is bound to plain POCO singletons rather than <c>IOptions&lt;T&gt;</c>,
    /// because the services consuming them live in the Application layer, which deliberately
    /// depends on next to nothing.
    /// </para>
    /// </summary>
    public static IServiceCollection AddProductionAndInspection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(
            configuration.GetSection(ProductionConfiguration.SectionName).Get<ProductionConfiguration>()
            ?? new ProductionConfiguration());

        services.AddSingleton(
            configuration.GetSection(InspectionConfiguration.SectionName).Get<InspectionConfiguration>()
            ?? new InspectionConfiguration());

        // Singleton so a configured seed yields one reproducible sequence for the process
        // rather than restarting per message.
        services.AddSingleton<IVerdictSource, RandomVerdictSource>();

        services.AddScoped<IProductionRunRepository, ProductionRunRepository>();
        services.AddScoped<IInspectionRunRepository, InspectionRunRepository>();

        services.AddScoped<ProductionService>();
        services.AddScoped<InspectionService>();

        return services;
    }

    /// <summary>
    /// Registers the Epic 7 shipping workflow: the shipment repository, its configuration, the
    /// carrier booking source, and the service that books and dispatches.
    /// <para>
    /// Both hosts need all of it, for the same reason as above. The worker books on
    /// <c>inspection-passed</c> and dispatches on <c>shipment-scheduled</c>; the API needs it
    /// because the manual booking endpoint drives the <em>same</em> workflow, and because
    /// releasing an order held at Delivery re-requests a booking (7.3).
    /// </para>
    /// </summary>
    public static IServiceCollection AddShipping(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(
            configuration.GetSection(ShippingConfiguration.SectionName).Get<ShippingConfiguration>()
            ?? new ShippingConfiguration());

        // Singleton so a configured seed yields one reproducible sequence for the process,
        // exactly as the verdict source does.
        services.AddSingleton<ICarrierBooking, ConfiguredCarrierBooking>();

        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<ShippingService>();

        return services;
    }

    /// <summary>
    /// Registers 13.3's sub-assembly loop: spawning a child work order for a short manufactured
    /// component, putting a finished child's units away as stock, and releasing the parent.
    /// <para>
    /// <strong>The worker only</strong>, unlike Epics 6 and 7's registrations — all three moments
    /// are stages, and none of them has an API path a visitor drives by hand. If one ever does, this
    /// call goes in the API too: <c>MaterialPickingService</c> takes <see cref="SubAssemblyService"/>
    /// as an <em>optional</em> dependency, so a host that resolved picking without it would quietly
    /// hold an order instead of making the missing part rather than failing to start.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSubAssemblies(this IServiceCollection services)
    {
        services.AddScoped<IComponentStockRepository, ComponentStockRepository>();
        services.AddScoped<SubAssemblyService>();
        services.AddScoped<StockPutawayService>();

        return services;
    }

    /// <summary>
    /// Registers the Epic 8 recovery surface: the dead-letter queries and the replay workflow.
    /// <para>
    /// Both hosts again, and for a new reason this time — the API serves
    /// <c>/system/dead-letters</c>, while the worker's parked-queue drain needs the concrete
    /// repository to write rows with. The concrete type is registered as well as the interface
    /// because the drain writes and the API reads, and only the reader deserves the abstraction.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDeadLetters(this IServiceCollection services)
    {
        services.AddScoped<DeadLetterRepository>();
        services.AddScoped<IDeadLetterRepository>(sp => sp.GetRequiredService<DeadLetterRepository>());
        services.AddScoped<DeadLetterService>();

        return services;
    }
}
