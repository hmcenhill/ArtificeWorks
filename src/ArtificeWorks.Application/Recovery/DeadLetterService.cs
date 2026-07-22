using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Recovery;

/// <summary>
/// Recovery, as a workflow (8.3). Looking at what failed is a query; putting it back is a write,
/// and it goes through the outbox like every other write in the system — the one endpoint whose
/// entire job is "reliably re-send this" would be an odd place to publish unreliably.
/// <para>
/// This is the same story 5.3's shortage hold and 7.3's carrier refusal already tell, told for
/// infrastructure failures instead of business ones: something stopped, a human can see why, and
/// a human presses the button that starts it moving again.
/// </para>
/// </summary>
public sealed class DeadLetterService
{
    private readonly IDeadLetterRepository _repository;
    private readonly IRawEventPublisher _publisher;
    private readonly ILogger<DeadLetterService> _logger;

    public DeadLetterService(
        IDeadLetterRepository repository,
        IRawEventPublisher publisher,
        ILogger<DeadLetterService> logger)
    {
        _repository = repository;
        _publisher = publisher;
        _logger = logger;
    }

    public Task<DeadLetterPageDto> List(
        int page, int pageSize, Guid? workOrderId, bool? replayed, CancellationToken cancellationToken = default)
        => _repository.GetPage(page, pageSize, workOrderId, replayed, cancellationToken);

    public Task<DeadLetterDetailDto?> Get(Guid id, CancellationToken cancellationToken = default)
        => _repository.Get(id, cancellationToken);

    /// <summary>
    /// Puts a dead letter back into <c>artifice.events</c> under its original routing key.
    /// <para>
    /// <strong>Replay resets the retry ladder.</strong> The republished message carries no attempt
    /// header, so it starts at attempt 1 with all three rungs ahead of it. A human deciding to try
    /// again is a new decision, not a continuation of the automated one that gave up.
    /// </para>
    /// <para>
    /// <strong>A second replay needs <paramref name="force"/>.</strong> Not because replaying
    /// twice is dangerous — the dedupe keys make a replayed <c>MaterialsReserved</c> for an
    /// already-picked order ack and skip, exactly as a redelivery does — but because a visitor
    /// clicking the button again usually means "did that work?", and the honest answer to that is
    /// a message saying it already ran, not another silent re-send.
    /// </para>
    /// </summary>
    public async Task<ReplayResult> Replay(Guid id, bool force, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.Get(id, cancellationToken);
        if (existing is null)
        {
            return new ReplayResult(ReplayOutcome.NotFound, $"No dead letter found with id {id}.");
        }

        if (existing.ReplayedUtc is not null && !force)
        {
            return new ReplayResult(ReplayOutcome.AlreadyReplayed,
                $"Dead letter {id} was already replayed at {existing.ReplayedUtc:O} " +
                $"({existing.ReplayCount} time(s)). Replay again with force=true if that is what you want.");
        }

        // Staged first; MarkReplayed's SaveChanges commits the outbox row and the stamp that says
        // it was sent in one transaction. 8.1's rule, applied to recovery: the record can never
        // claim a replay that was never queued, nor queue one it doesn't admit to.
        await _publisher.EnqueueAsync(existing.EventType, existing.Payload, existing.CorrelationId, cancellationToken);

        var record = await _repository.MarkReplayed(id, cancellationToken);
        if (record is null)
        {
            // Swept away between the read and the write.
            return new ReplayResult(ReplayOutcome.NotFound, $"No dead letter found with id {id}.");
        }

        _logger.LogInformation(
            "Replayed dead letter {DeadLetterId} ({EventType}) [correlation {CorrelationId}]; replay #{ReplayCount}.",
            id, record.EventType, record.CorrelationId, record.ReplayCount);

        return new ReplayResult(ReplayOutcome.Replayed,
            $"Dead letter {id} republished as {record.EventType}; the retry ladder starts over.");
    }
}

public enum ReplayOutcome
{
    Replayed,
    NotFound,
    AlreadyReplayed
}

public sealed record ReplayResult(ReplayOutcome Outcome, string Summary);
