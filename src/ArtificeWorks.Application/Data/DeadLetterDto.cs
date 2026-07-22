namespace ArtificeWorks.Application.Data;

/// <summary>
/// One row of the dead-letter list. Shaped for a list row the way 7.4's timeline was shaped for a
/// trace view — this is the second thing Epic 11's dashboard renders, and the fields are the ones
/// a person scanning a table of failures actually needs: what failed, whose order it was, how
/// hard the system already tried, and the first line of why.
/// </summary>
public sealed record DeadLetterSummaryDto(
    Guid Id,
    string EventType,
    Guid CorrelationId,
    Guid? WorkOrderId,
    int Attempts,
    string Error,
    DateTime ParkedUtc,
    DateTime? ReplayedUtc,
    int ReplayCount);

/// <summary>
/// The whole record, payload included, for a human to read before deciding whether to put it
/// back. Deliberately a separate shape from the summary: the payload can be large and nobody
/// wants twenty of them in a list.
/// </summary>
public sealed record DeadLetterDetailDto(
    Guid Id,
    string EventType,
    Guid CorrelationId,
    Guid? WorkOrderId,
    int Attempts,
    string Error,
    DateTime ParkedUtc,
    DateTime? ReplayedUtc,
    int ReplayCount,
    string Payload);

/// <summary>A page of dead letters, newest first.</summary>
public sealed record DeadLetterPageDto(
    IReadOnlyList<DeadLetterSummaryDto> Items,
    int Page,
    int PageSize,
    int TotalCount);
