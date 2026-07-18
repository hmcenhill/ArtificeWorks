using ArtificeWorks.Application.Data;

namespace ArtificeWorks.Application.Commands;

public enum CreateProductOutcome
{
    Success,
    AlreadyExists,
    Error
}

public class CreateProductResponse
{
    public bool IsSuccess => Outcome == CreateProductOutcome.Success;
    public CreateProductOutcome Outcome { get; init; }
    public ProductDto? Product { get; init; }
    public string? Error { get; init; }
}
