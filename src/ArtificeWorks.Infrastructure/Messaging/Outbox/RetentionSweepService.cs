using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.Infrastructure.Messaging.Outbox;

/// <summary>
/// Ages out the bookkeeping tables this epic added, so "keep the row as evidence" doesn't quietly
/// become "keep every row forever": sent outbox rows (8.1), spent idempotency keys (8.4) and
/// replayed dead letters (8.3).
/// <para>
/// Nothing unsent, unreplayed, or unresolved is ever swept — the sweep only removes records whose
/// job is finished. A dead letter nobody has replayed is a failure waiting for a human and stays
/// until they deal with it.
/// </para>
/// </summary>
public sealed class RetentionSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxConfiguration _config;
    private readonly ILogger<RetentionSweepService> _logger;

    public RetentionSweepService(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxConfiguration> config,
        ILogger<RetentionSweepService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_config.SweepIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Retention sweep failed; it will run again next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        var now = DateTime.UtcNow;

        var outboxCutoff = now.AddHours(-_config.SentRetentionHours);
        var sentRemoved = await context.OutboxMessages
            .Where(m => m.SentUtc != null && m.SentUtc < outboxCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var keyCutoff = now.AddHours(-_config.IdempotencyKeyRetentionHours);
        var keysRemoved = await context.IdempotencyKeys
            .Where(k => k.CreatedUtc < keyCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var deadLetterCutoff = now.AddHours(-_config.DeadLetterRetentionHours);
        var deadLettersRemoved = await context.DeadLetters
            .Where(d => d.ReplayedUtc != null && d.ReplayedUtc < deadLetterCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (sentRemoved + keysRemoved + deadLettersRemoved > 0)
        {
            _logger.LogInformation(
                "Retention sweep removed {SentRows} sent outbox row(s), {Keys} idempotency key(s) and {DeadLetters} replayed dead letter(s).",
                sentRemoved, keysRemoved, deadLettersRemoved);
        }
    }
}
