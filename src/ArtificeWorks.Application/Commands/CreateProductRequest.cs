using System.ComponentModel.DataAnnotations;

namespace ArtificeWorks.Application.Commands;

public class CreateProductRequest
{
    [Required]
    public required string Requestor { get; set; }

    [Required]
    public required string ProductId { get; set; }

    [Required]
    public required string ProductName { get; set; }
}
