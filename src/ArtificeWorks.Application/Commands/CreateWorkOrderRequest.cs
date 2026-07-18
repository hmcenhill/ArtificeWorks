using System.ComponentModel.DataAnnotations;

namespace ArtificeWorks.Application.Commands;

public class CreateWorkOrderRequest
{
    [Required]
    public required string Requestor { get; set; }

    [Required]
    public required string ItemId { get; set; }

    [Range(1, uint.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public uint Qty { get; set; }

    public string? Notes { get; set; }
}
