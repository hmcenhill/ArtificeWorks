using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Production;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Scheduling;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.Infrastructure.Simulation;

public static class SimulationServiceCollectionExtensions
{
    /// <summary>
    /// The dials, the cache in front of them, the task that keeps it fresh, and the pace policy
    /// that reads it (10.1, 10.2). <strong>All three hosts call this</strong> — a setting only one
    /// process can see is not a setting, it is a local variable.
    /// </summary>
    public static IServiceCollection AddSimulationSettings(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SimulationConfiguration>(configuration.GetSection(SimulationConfiguration.SectionName));
        services.Configure<PaceConfiguration>(configuration.GetSection(PaceConfiguration.SectionName));

        // The appsettings-derived defaults, assembled once. They seed the row on a fresh database
        // and back the cache until the first read succeeds — so a host that cannot reach Postgres
        // still behaves as configured rather than as a blank record.
        var defaults = DefaultsFrom(configuration);
        services.AddSingleton(defaults);
        services.AddSingleton(new SimulationSettingsCache(defaults));

        services.AddScoped<ISimulationSettingsRepository, SimulationSettingsRepository>();
        services.AddScoped<SimulationSettingsService>();

        services.AddScheduledTask<SimulationSettingsRefreshTask>();

        // Singleton and stateless-but-for-its-generator, exactly like the raw publisher: the outbox
        // dispatcher is a background loop with no scope to resolve from. AddRabbitMqConnection
        // TryAdds the same pair so a messaging-only host still has a working (off) policy; the
        // registration here is the one that carries the configured defaults, and it wins whichever
        // order the two are called in.
        services.AddSingleton<IPacePolicy, PacePolicy>();

        return services;
    }

    /// <summary>
    /// Folds the existing <c>Inspection</c>, <c>Shipping</c>, <c>Production</c> and <c>Pace</c>
    /// sections into one settings record.
    /// <para>
    /// <strong>Nothing was renamed.</strong> <c>Inspection:FailureRate</c> and
    /// <c>Shipping:RefusalRate</c> keep the meaning and the documentation they were given in 6.2
    /// and 7.3 — they just stop being the last word, because the row overrides them.
    /// </para>
    /// </summary>
    public static SimulationSettings DefaultsFrom(IConfiguration configuration)
    {
        var inspection = configuration.GetSection(InspectionConfiguration.SectionName).Get<InspectionConfiguration>()
            ?? new InspectionConfiguration();
        var shipping = configuration.GetSection(ShippingConfiguration.SectionName).Get<ShippingConfiguration>()
            ?? new ShippingConfiguration();
        var production = configuration.GetSection(ProductionConfiguration.SectionName).Get<ProductionConfiguration>()
            ?? new ProductionConfiguration();

        var shipped = SimulationSettings.ShippedDefaults;
        var section = configuration.GetSection(SimulationConfiguration.SectionName);

        return new SimulationSettings
        {
            PacingEnabled = section.GetValue("PacingEnabled", shipped.PacingEnabled),
            PaceSecondsScheduled = section.GetValue("PaceSecondsScheduled", shipped.PaceSecondsScheduled),
            PaceSecondsMaterialsReserved =
                section.GetValue("PaceSecondsMaterialsReserved", shipped.PaceSecondsMaterialsReserved),
            PaceSecondsProductionCompleted =
                section.GetValue("PaceSecondsProductionCompleted", shipped.PaceSecondsProductionCompleted),
            PaceSecondsReworkRequired = section.GetValue("PaceSecondsReworkRequired", shipped.PaceSecondsReworkRequired),
            PaceSecondsInspectionPassed =
                section.GetValue("PaceSecondsInspectionPassed", shipped.PaceSecondsInspectionPassed),
            PaceSecondsShipmentScheduled =
                section.GetValue("PaceSecondsShipmentScheduled", shipped.PaceSecondsShipmentScheduled),
            PaceJitter = section.GetValue("PaceJitter", shipped.PaceJitter),

            FailureRate = inspection.FailureRate,
            AutoInspect = inspection.AutoInspect,
            RefusalRate = shipping.RefusalRate,
            AutoBook = shipping.AutoBook,
            MaxRebuildAttempts = production.MaxRebuildAttempts,

            GenerationEnabled = section.GetValue("GenerationEnabled", shipped.GenerationEnabled),
            GenerationIntervalSeconds =
                section.GetValue("GenerationIntervalSeconds", shipped.GenerationIntervalSeconds),
            MaxInFlight = section.GetValue("MaxInFlight", shipped.MaxInFlight),

            WorldSweepIntervalHours = section.GetValue("WorldSweepIntervalHours", shipped.WorldSweepIntervalHours),
            RetireAfterHours = section.GetValue("RetireAfterHours", shipped.RetireAfterHours),
        };
    }

    /// <summary>
    /// The world sweep (10.4): the repository that does it and the service that logs and counts it.
    /// The API needs it for <c>POST /system/world/reset</c>; the simulation host needs it for the
    /// schedule. Both run the <em>same</em> code, which is the story's first acceptance criterion.
    /// </summary>
    public static IServiceCollection AddWorldReset(this IServiceCollection services)
    {
        services.AddScoped<IWorldRepository, WorldRepository>();
        services.AddScoped<WorldResetService>();
        return services;
    }
}
