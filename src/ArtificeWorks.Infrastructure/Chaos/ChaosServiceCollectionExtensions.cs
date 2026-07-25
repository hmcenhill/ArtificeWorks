using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArtificeWorks.Infrastructure.Chaos;

public static class ChaosServiceCollectionExtensions
{
    /// <summary>
    /// The injected-fault registry and the service in front of it (Epic 12).
    /// <para>
    /// The API needs both — it arms faults through <c>POST /system/chaos</c>. The worker needs the
    /// <em>registry</em>, which its inspection service consults to fire a <c>FailInspection</c> fault
    /// and its picking service consults to fire a <c>TransientOnce</c> or <c>Poison</c> fault (12.2);
    /// the service travels along harmlessly. The repository is <c>TryAdd</c>ed so this can be called
    /// alongside <c>AddWorldReset</c> (which needs the same registry for its disarm sweep) without a
    /// duplicate registration.
    /// </para>
    /// </summary>
    public static IServiceCollection AddChaos(this IServiceCollection services)
    {
        services.TryAddScoped<IInjectedFaultRepository, InjectedFaultRepository>();
        services.AddScoped<ChaosService>();
        return services;
    }
}
