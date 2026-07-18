namespace ArtificeWorks.Application.Commands;

/// <summary>
/// Payload for a work order lifecycle command (advance / hold / release):
/// who is performing it and any optional context to record in state history.
/// </summary>
public class WorkOrderCommandRequest
{
    public required string CreatedBy { get; set; }
    public string? Notes { get; set; }
}
