using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Infrastructure.Scheduling;
using ArtificeWorks.Infrastructure.Simulation;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.Simulation.Tasks;

/// <summary>
/// Runs the world sweep on a schedule (10.4) — the half of the story that makes 10.3's generator
/// safe to leave running unattended. Together they are the difference between a demo that survives
/// a month and one that has to be restarted before each viewing.
/// <para>
/// It is a thin shell on purpose: everything it does is <see cref="WorldResetService.Sweep"/>,
/// which is the same method <c>POST /system/world/reset</c> calls. The schedule and the button are
/// the same action.
/// </para>
/// </summary>
public sealed class WorldResetTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimulationSettingsCache _settings;
    private readonly SimulationConfiguration _config;

    public WorldResetTask(
        IServiceScopeFactory scopeFactory,
        SimulationSettingsCache settings,
        IOptions<SimulationConfiguration> config)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _config = config.Value;
    }

    public string Name => "world-reset";

    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, _settings.Current.WorldSweepIntervalHours));

    /// <summary>
    /// False, and this one matters: a destructive sweep that ran at startup would fire on every
    /// deploy and every crash-restart. It waits out its interval.
    /// </summary>
    public bool RunOnStartup => false;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_config.WorldSweepEnabled)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var world = scope.ServiceProvider.GetRequiredService<WorldResetService>();

        await world.Sweep("schedule", cancellationToken);
    }
}
