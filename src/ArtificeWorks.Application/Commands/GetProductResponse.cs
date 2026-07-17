using ArtificeWorks.Application.Data;

namespace ArtificeWorks.Application.Commands;

public class GetProductResponse
{
    public bool IsSuccess { get; init; }
    public ProductDto? Product { get; init; }
    public string? Error { get; init; }
}
