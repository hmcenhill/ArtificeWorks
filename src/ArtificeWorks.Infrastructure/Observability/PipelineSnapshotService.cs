using ArtificeWorks.Application.Observability;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Scheduling;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.Infrastructure.Observability;

/// <summary>
/// Refreshes the <see cref="PipelineSnapshot"/> behind 9.2's observable gauges and
/// <c>GET /system/stats</c>.
/// <para>
/// <strong>This exists so no metric collection issues a database query.</strong> An observable
/// gauge's callback runs on the meter's collection thread, in root scope — it cannot resolve a
/// scoped <c>DbContext</c>, and if it could, a Grafana scrape interval would become a write-free
/// but very real load generator against Postgres, one <c>SELECT</c> per gauge per scrape. One
/// timer, one set of queries, one immutable snapshot that every reader shares.
/// </para>
/// <para>
/// <strong>It never throws upward.</strong> A failed refresh leaves the previous snapshot in place
/// and logs; stale gauges are a much better outcome than a background service that dies and takes
/// the numbers with it silently — the exact failure mode Epic 8 spent itself removing.
/// </para>
/// <para>
/// <strong>It was the third hand-rolled background loop, and it is now a scheduled task</strong>
/// (10.1). The note that used to sit here — "worth folding into one timer host before a fourth
/// arrives" — is cashed: this runs on <c>PeriodicTaskHost</c> along with the simulation's tasks.
/// It could not simply <em>move</em> to the simulation host, because <c>GET /system/stats</c>
/// reads the snapshot in the API and the gauges read it in the worker; all three hosts refresh
/// their own, sharing the type rather than the process.
/// </para>
/// </summary>
public sealed class PipelineSnapshotService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PipelineSnapshotCache _cache;
    private readonly TelemetryConfiguration _config;
    private readonly ILogger<PipelineSnapshotService> _logger;

    public PipelineSnapshotService(
        IServiceScopeFactory scopeFactory,
        PipelineSnapshotCache cache,
        IOptions<TelemetryConfiguration> config,
        ILogger<PipelineSnapshotService> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _config = config.Value;
        _logger = logger;
    }

    public string Name => "pipeline-snapshot";

    public TimeSpan Interval => TimeSpan.FromMilliseconds(_config.SnapshotIntervalMs);

    public Task RunAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    /// <summary>Takes one reading. Public so a test can take it deterministically rather than waiting.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

            var byStatus = await context.WorkOrders
                .AsNoTracking()
                .GroupBy(order => order.CurrentStatus)
                .Select(group => new { Status = group.Key, Count = group.LongCount() })
                .ToListAsync(cancellationToken);

            var unsent = await context.OutboxMessages
                .AsNoTracking()
                .Where(message => message.SentUtc == null)
                .LongCountAsync(cancellationToken);

            // The oldest unsent row's age IS the lag. Null when the outbox is empty, which reads
            // as zero seconds behind — the honest answer for "nothing is waiting".
            var oldestUnsent = unsent == 0
                ? (DateTime?)null
                : await context.OutboxMessages
                    .AsNoTracking()
                    .Where(message => message.SentUtc == null)
                    .MinAsync(message => (DateTime?)message.OccurredUtc, cancellationToken);

            var unreplayed = await context.DeadLetters
                .AsNoTracking()
                .Where(letter => letter.ReplayedUtc == null)
                .LongCountAsync(cancellationToken);

            // 10.3. Grouped in the database rather than derived from the status counts, because
            // "how much of this is real demand?" needs the origin on both totals and in-flight —
            // and a dashboard showing simulated traffic as throughput is a lie.
            var byOrigin = await context.WorkOrders
                .AsNoTracking()
                .GroupBy(order => new { order.Origin, order.CurrentStatus })
                .Select(group => new { group.Key.Origin, group.Key.CurrentStatus, Count = group.LongCount() })
                .ToListAsync(cancellationToken);

            // 10.4's gauge. Two scalars, not a per-component breakdown: this is "are the shelves
            // full?", and per-component levels are a query someone runs, not a metric.
            var stock = await context.Components
                .AsNoTracking()
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    OnHand = group.Sum(component => (long)component.OnHand),
                    Seed = group.Sum(component => (long)component.SeedOnHand)
                })
                .FirstOrDefaultAsync(cancellationToken);

            _cache.Update(new PipelineSnapshot(
                CapturedUtc: DateTime.UtcNow,
                WorkOrdersByStatus: byStatus.ToDictionary(entry => entry.Status.ToString(), entry => entry.Count),
                UnsentOutboxRows: unsent,
                OutboxLagSeconds: oldestUnsent is null
                    ? 0
                    : Math.Max(0, (DateTime.UtcNow - DateTime.SpecifyKind(oldestUnsent.Value, DateTimeKind.Utc)).TotalSeconds),
                UnreplayedDeadLetters: unreplayed,
                TotalWorkOrders: byStatus.Sum(entry => entry.Count),
                WorkOrdersByOrigin: byOrigin
                    .GroupBy(entry => entry.Origin.ToString())
                    .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count)),
                InFlightByOrigin: byOrigin
                    .Where(entry => entry.CurrentStatus is not (WorkOrderStatus.Completed or WorkOrderStatus.Cancelled))
                    .GroupBy(entry => entry.Origin.ToString())
                    .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count)),
                // An empty catalog reads as a full factory rather than an empty one: nothing is
                // short when nothing is stocked, and a 0 here would alarm a dashboard for no reason.
                StockLevelRatio: stock is null || stock.Seed <= 0 ? 1 : Math.Clamp((double)stock.OnHand / stock.Seed, 0, 1)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception e)
        {
            // Stale gauges, not dead ones. Debug rather than Warning because a snapshot failing
            // while the database is down would otherwise emit a line every few seconds on top of
            // whatever is already shouting about the database.
            _logger.LogDebug(e, "Pipeline snapshot refresh failed; keeping the previous reading.");
        }
    }
}
