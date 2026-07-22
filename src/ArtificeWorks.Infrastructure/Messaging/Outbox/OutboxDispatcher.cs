using System.Diagnostics;

using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.Infrastructure.Messaging.Outbox;

/// <summary>
/// Drains <c>outbox_messages</c> to RabbitMQ. Runs in <em>both</em> hosts, because both write
/// outbox rows: the API is where the pipeline starts, and a dropped <c>work-order.scheduled</c>
/// strands an order before it has moved at all.
/// <para>
/// <strong>The claim is <c>FOR UPDATE SKIP LOCKED</c>.</strong> Two dispatchers can therefore
/// never hold the same row, so each row publishes exactly once per instance-pair — and a row
/// that some other instance is chewing on doesn't block the ones behind it.
/// </para>
/// <para>
/// <strong>This makes publishing at-least-once, deliberately.</strong> The loop can die between
/// <c>BasicPublish</c> and the <c>SaveChanges</c> that marks the row sent, so a row can go out
/// twice. That is the correct trade: a duplicate is answered by the dedupe keys Epics 5–7 built
/// at every stage, a loss is answered by nothing. The outbox does not make delivery exactly-once
/// — it converts <em>loss</em> into <em>duplication</em>, and this system was built to absorb
/// duplication.
/// </para>
/// <para>
/// <strong>Ordering.</strong> Rows are claimed and published in <see cref="OutboxMessage.Id"/>
/// order, so one host's events leave in the order it wrote them. Across two hosts the interleave
/// is unconstrained — but consecutive events for one work order are separated by an actual
/// delivery and a handler, so in practice a work order's events are written by one host at a
/// time.
/// </para>
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxConfiguration _config;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxConfiguration> config,
        ArtificeWorksMetrics metrics,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config.Value;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox dispatcher started; polling every {PollMs}ms in batches of {BatchSize}.",
            _config.PollIntervalMs, _config.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep going while a full batch comes back — a backlog after a broker outage
                // should drain at broker speed, not at one batch per poll.
                int dispatched;
                do
                {
                    dispatched = await DispatchBatchAsync(stoppingToken);
                }
                while (dispatched == _config.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The loop itself must never die: a dispatcher that stops is silence, which is
                // the exact failure mode this epic exists to remove.
                _logger.LogError(e, "Outbox dispatch pass failed; retrying after the poll interval.");
            }

            try
            {
                await Task.Delay(_config.PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Claims, publishes and marks one batch. Returns how many rows were claimed.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
        var broker = scope.ServiceProvider.GetRequiredService<IBrokerPublisher>();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED is doing the concurrency control here, so the rows come back already
        // exclusive to this dispatcher for the life of the transaction. They are tracked, so the
        // MarkSent/RecordFailure calls below flush on SaveChanges.
        // Eligibility is compared against OUR clock, not Postgres's `now()`. Every timestamp on
        // the row is written by .NET, so mixing in the database's clock was a cross-clock
        // comparison: a container whose clock lags the host's by a few milliseconds — routine on
        // Docker Desktop — makes a row with a zero backoff ineligible for a moment, which showed
        // up as an outbox test that only failed under load. One clock, one answer.
        var eligibleAsOf = DateTime.UtcNow;

        var claimed = await context.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE "SentUtc" IS NULL
                  AND ("NextAttemptUtc" IS NULL OR "NextAttemptUtc" <= {eligibleAsOf})
                ORDER BY "Id"
                LIMIT {_config.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        foreach (var message in claimed)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();

                await broker.PublishRawAsync(
                    message.EventType, message.Payload, message.EventId, message.CorrelationId,
                    headers: null, parentContext: RestoreTraceContext(message), cancellationToken);

                _metrics.OutboxPublished(
                    message.EventType, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                message.MarkSent(DateTime.UtcNow);
            }
            catch (Exception e)
            {
                var backoff = BackoffFor(message.Attempts);
                message.RecordFailure(e.Message, DateTime.UtcNow.Add(backoff));

                _logger.LogError(e,
                    "Outbox row {OutboxId} ({EventType}, correlation {CorrelationId}) failed to publish on attempt "
                    + "{Attempts}; retrying in {BackoffSeconds}s. The event is not lost.",
                    message.Id, message.EventType, message.CorrelationId, message.Attempts, backoff.TotalSeconds);

                // Stop the batch: if the broker is down, the rest will fail identically, and
                // publishing later rows past a stuck earlier one would reorder the stream.
                break;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var sent = claimed.Count(m => m.SentUtc is not null);
        if (sent > 0)
        {
            _logger.LogDebug("Outbox dispatched {SentCount} of {ClaimedCount} claimed row(s).", sent, claimed.Count);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Rebuilds the activity context the row was staged under (9.1) so the producer span becomes a
    /// child of the request or delivery that caused the event, rather than the root of a fresh
    /// one-span trace.
    /// <para>
    /// <c>isRemote: true</c> because it is: this thread is not the one that made it, and the gap
    /// between the two is a database row and up to a poll interval. A row with no captured context
    /// — staged by a background service, or by a test with no listener attached — returns null and
    /// publishes untraced. Untraced, never broken: that is the rule.
    /// </para>
    /// </summary>
    private static ActivityContext? RestoreTraceContext(OutboxMessage message) =>
        ActivityContext.TryParse(message.TraceParent, message.TraceState, isRemote: true, out var context)
            ? context
            : null;

    private TimeSpan BackoffFor(int attempts)
    {
        // attempts has already been incremented by RecordFailure's caller order — it is the count
        // *including* this failure, so attempt 1 waits the initial backoff.
        var seconds = _config.InitialBackoffSeconds * Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, _config.MaxBackoffSeconds));
    }
}
