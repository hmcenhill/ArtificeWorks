using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// This is the third background loop next to <c>OutboxDispatcher</c> and
/// <c>RetentionSweepService</c>. Worth folding into one timer host before a fourth arrives.
/// </para>
/// </summary>
public sealed class PipelineSnapshotService : BackgroundService
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Pipeline snapshot refreshing every {IntervalMs}ms.", _config.SnapshotIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);

            try
            {
                await Task.Delay(_config.SnapshotIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

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

            _cache.Update(new PipelineSnapshot(
                CapturedUtc: DateTime.UtcNow,
                WorkOrdersByStatus: byStatus.ToDictionary(entry => entry.Status.ToString(), entry => entry.Count),
                UnsentOutboxRows: unsent,
                OutboxLagSeconds: oldestUnsent is null
                    ? 0
                    : Math.Max(0, (DateTime.UtcNow - DateTime.SpecifyKind(oldestUnsent.Value, DateTimeKind.Utc)).TotalSeconds),
                UnreplayedDeadLetters: unreplayed,
                TotalWorkOrders: byStatus.Sum(entry => entry.Count)));
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
