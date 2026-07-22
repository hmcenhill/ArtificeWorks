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
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxConfiguration> config,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config.Value;
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
        var claimed = await context.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE "SentUtc" IS NULL
                  AND ("NextAttemptUtc" IS NULL OR "NextAttemptUtc" <= now())
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
                await broker.PublishRawAsync(
                    message.EventType, message.Payload, message.EventId, message.CorrelationId,
                    headers: null, cancellationToken);

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

    private TimeSpan BackoffFor(int attempts)
    {
        // attempts has already been incremented by RecordFailure's caller order — it is the count
        // *including* this failure, so attempt 1 waits the initial backoff.
        var seconds = _config.InitialBackoffSeconds * Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, _config.MaxBackoffSeconds));
    }
}
