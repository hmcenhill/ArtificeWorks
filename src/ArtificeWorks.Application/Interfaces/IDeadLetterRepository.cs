using ArtificeWorks.Application.Data;

namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// Reads and marks dead letters (8.3). The rows themselves live in Infrastructure — a parked
/// message is a fact about the transport, not about the factory — so this interface trades in
/// DTOs and a narrow replay operation rather than exposing the entity.
/// </summary>
public interface IDeadLetterRepository
{
    Task<DeadLetterPageDto> GetPage(
        int page,
        int pageSize,
        Guid? workOrderId,
        bool? replayed,
        CancellationToken cancellationToken = default);

    Task<DeadLetterDetailDto?> Get(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the record as replayed and commits — <em>together</em> with whatever the caller has
    /// staged on the same unit of work, which is the outbox row carrying the message back out.
    /// The mark and the re-send are one transaction, so a record can never claim to have been
    /// replayed by something that was never sent.
    /// </summary>
    Task<DeadLetterReplayInfo?> MarkReplayed(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>What a caller needs to re-publish a dead letter: the original key, bytes and thread.</summary>
public sealed record DeadLetterReplayInfo(string EventType, string Payload, Guid CorrelationId, int ReplayCount);
