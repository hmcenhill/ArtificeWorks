using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Infrastructure.Scheduling;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.Infrastructure.Simulation;

/// <summary>
/// Keeps this host's <see cref="SimulationSettingsCache"/> in step with the stored row (10.2).
/// Registered in all three hosts, which is the whole point: a <c>PUT</c> handled by the API is
/// meaningless unless the worker — where inspections actually fail — sees it too.
/// <para>
/// On the first pass it also <em>seeds</em> the row from configuration if nobody has written one,
/// so a fresh database behaves exactly as appsettings documents and 6.2/7.3 describe. Thereafter it
/// only reads: a re-boot must not stomp a value someone set live.
/// </para>
/// <para>
/// A failed refresh keeps the previous values and logs at Debug — stale dials, not dead ones, and
/// the same rule <c>PipelineSnapshotService</c> follows for the same reason: a background loop that
/// dies takes the numbers with it silently.
/// </para>
/// </summary>
public sealed class SimulationSettingsRefreshTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimulationSettingsCache _cache;
    private readonly SimulationSettings _configured;
    private readonly SimulationConfiguration _config;
    private readonly ILogger<SimulationSettingsRefreshTask> _logger;

    private bool _seeded;

    /// <param name="configured">
    /// The appsettings-derived defaults. They are what the row is created from on a fresh
    /// database, and what this host runs on until the first successful read.
    /// </param>
    public SimulationSettingsRefreshTask(
        IServiceScopeFactory scopeFactory,
        SimulationSettingsCache cache,
        SimulationSettings configured,
        IOptions<SimulationConfiguration> config,
        ILogger<SimulationSettingsRefreshTask> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _configured = configured;
        _config = config.Value;
        _logger = logger;
    }

    public string Name => "simulation-settings-refresh";

    public TimeSpan Interval => TimeSpan.FromMilliseconds(_config.SettingsRefreshMs);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISimulationSettingsRepository>();

            if (!_seeded)
            {
                var seeded = await repository.SeedIfMissing(_configured, cancellationToken);
                _cache.Update(seeded);
                _seeded = true;

                _logger.LogInformation(
                    "Simulation settings loaded (pacing {Pacing}, failure rate {FailureRate}, generation {Generation}); "
                    + "refreshing every {IntervalMs}ms.",
                    seeded.PacingEnabled ? "on" : "off", seeded.FailureRate,
                    seeded.GenerationEnabled ? "on" : "off", _config.SettingsRefreshMs);
                return;
            }

            if (await repository.Get(cancellationToken) is { } current)
            {
                _cache.Update(current);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Simulation settings refresh failed; keeping the previous values.");
        }
    }
}
