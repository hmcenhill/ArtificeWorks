using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Shipping;
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
}
