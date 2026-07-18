using ArtificeWorks.Application.Data;

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
    public string? Error { get; init; }
}
