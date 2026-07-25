using System.Diagnostics;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Observability;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Simulation;

/// <param name="RetiredBeforeUtc">The cutoff actually used, so the caller can see what "old" meant.</param>
/// <param name="FaultsDisarmed">Armed-but-unfired injected faults cleared (Epic 12), so a leftover fault never outlives a reset.</param>
public sealed record WorldResetResult(
    int ComponentsRestocked,
    int OrdersRetired,
    int FaultsDisarmed,
    DateTime RetiredBeforeUtc,
    string Summary);

/// <summary>
/// Keeps a permanently-running shared factory habitable (10.4).
/// <para>
/// Everything before Epic 10 assumed a factory that gets restarted. A demo on a home server does
/// not: it runs for months with strangers wandering through it, and two slow leaks eventually ruin
/// it. <strong>Inventory</strong> — <c>CatalogSeeder</c> deliberately never restocks, which is
/// exactly right for a seeder and fatal for a shared world, so with 10.3's generator running the
/// shelves empty on their own and every new order holds at picking with a shortage. And
/// <strong>accumulation</strong> — thousands of Completed orders make the board useless,
/// <c>/system/stats</c> meaningless and the timeline query slow.
/// </para>
/// <para>
/// <strong>The scheduled sweep and <c>POST /system/world/reset</c> run this same method</strong>,
/// which is the story's first acceptance criterion and the reason there is no second code path to
/// keep in step.
/// </para>
/// <para>
/// <strong>It does not reset the settings row.</strong> "Reset the world" means the factory floor,
/// not the dials someone deliberately turned.
/// </para>
/// </summary>
public sealed class WorldResetService
{
    private readonly IWorldRepository _world;
    private readonly IInjectedFaultRepository _injectedFaults;
    private readonly SimulationSettingsCache _settings;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<WorldResetService> _logger;

    public WorldResetService(
        IWorldRepository world,
        IInjectedFaultRepository injectedFaults,
        SimulationSettingsCache settings,
        ArtificeWorksMetrics metrics,
        ILogger<WorldResetService> logger)
    {
        _world = world;
        _injectedFaults = injectedFaults;
        _settings = settings;
        _metrics = metrics;
        _logger = logger;
    }

    /// <param name="triggeredBy">Who asked — the schedule or a name from the endpoint. It goes in the log line.</param>
    public async Task<WorldResetResult> Sweep(string triggeredBy, CancellationToken cancellationToken = default)
    {
        // Its own span, so a slow sweep is visible in Tempo as a slow thing rather than as a
        // mysterious burst of deletes attributed to nothing.
        using var activity = ArtificeWorksTelemetry.ActivitySource.StartActivity(
            "world reset", ActivityKind.Internal);

        var cutoff = DateTime.UtcNow.AddHours(-_settings.Current.RetireAfterHours);
        activity?.SetTag("artificeworks.retire_before", cutoff.ToString("O"));

        var counts = await _world.Sweep(cutoff, cancellationToken);

        // Injected faults are the same class of housekeeping as retired orders (Epic 12): an
        // armed-but-unfired fault must not outlive a reset. A fired fault is left alone — it is
        // provenance, and it ages out with its order's retirement cascade above. Kept as its own
        // operation, outside the world sweep's carefully deadlock-ordered transaction, because it
        // touches an unrelated table and has no ordering to agree with.
        var faultsDisarmed = await _injectedFaults.DisarmUnfired(cancellationToken);

        // Counted after the commit, 9.2's rule: a sweep that rolled back must move no counter.
        _metrics.WorldSwept(counts.OrdersRetired, counts.ComponentsRestocked);

        activity?.SetTag("artificeworks.orders_retired", counts.OrdersRetired);
        activity?.SetTag("artificeworks.components_restocked", counts.ComponentsRestocked);
        activity?.SetTag("artificeworks.faults_disarmed", faultsDisarmed);

        var summary =
            $"Restocked {counts.ComponentsRestocked} component(s), retired {counts.OrdersRetired} order(s) "
            + $"and disarmed {faultsDisarmed} injected fault(s), last touched before {cutoff:u}.";

        // Information with its counts, always — even a no-op sweep. It is infrequent, it is
        // destructive, and it is the first thing to suspect when an order someone was watching is
        // gone; "the sweep ran and removed nothing" is exactly as useful an answer as the other one.
        _logger.LogInformation(
            "World reset ({TriggeredBy}): restocked {ComponentsRestocked} component(s), retired {OrdersRetired} order(s), "
            + "disarmed {FaultsDisarmed} injected fault(s), last touched before {Cutoff:u}.",
            triggeredBy, counts.ComponentsRestocked, counts.OrdersRetired, faultsDisarmed, cutoff);

        return new WorldResetResult(
            counts.ComponentsRestocked, counts.OrdersRetired, faultsDisarmed, cutoff, summary);
    }
}
