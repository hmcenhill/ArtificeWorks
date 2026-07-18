using ArtificeWorks.Application.Data;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Commands;

public enum WorkOrderCommandOutcome
{
    Success,
    NotFound,
    Rejected
}

public class WorkOrderCommandResponse
{
    public WorkOrderCommandOutcome Outcome { get; init; }
    public WorkOrderDto? WorkOrder { get; init; }

    /// <summary>The domain rejection code; set only when <see cref="Outcome"/> is <see cref="WorkOrderCommandOutcome.Rejected"/>.</summary>
    public TransitionErrorCode? ReasonCode { get; init; }

    /// <summary>Human-readable explanation, surfaced as the ProblemDetails detail.</summary>
    public string? Error { get; init; }
}
