using System.ComponentModel.DataAnnotations;

namespace ArtificeWorks.Application.Commands;

/// <summary>
/// Payload for a work order lifecycle command (advance / hold / release / cancel):
/// who is performing it and any optional context to record in state history.
/// </summary>
public class WorkOrderCommandRequest
{
    [Required]
    public required string CreatedBy { get; set; }

    public string? Notes { get; set; }
}
