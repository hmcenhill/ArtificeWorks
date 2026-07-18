using ArtificeWorks.Application.Data;

namespace ArtificeWorks.Application.Commands;

public enum CreateWorkOrderOutcome
{
    Success,
    ProductNotFound,
    Error
}

public class CreateWorkOrderResponse
{
    public bool IsSuccess => Outcome == CreateWorkOrderOutcome.Success;
    public CreateWorkOrderOutcome Outcome { get; init; }
    public WorkOrderDto? WorkOrder { get; init; }
    public string? Error { get; init; }
}
