using ArtificeWorks.Application.Data;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Commands;

public class CreateWorkOrderResponse
{
    public bool IsSuccess { get; init; }
    public WorkOrderDto? WorkOrder { get; init; }
    public string? Error { get; init; }
}
