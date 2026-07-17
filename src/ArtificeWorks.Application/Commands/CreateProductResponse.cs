using ArtificeWorks.Application.Data;

namespace ArtificeWorks.Application.Commands;

public class CreateProductResponse
{
    public bool IsSuccess { get; set; }
    public ProductDto? Product { get; set; }
    public string? Error { get; set; }
}