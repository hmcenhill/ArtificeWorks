using ArtificeWorks.Application.Data;

namespace ArtificeWorks.Application.Commands;

/// <summary>The outcome of exploding a product's bill of materials (13.2).</summary>
public enum GetProductBomOutcome
{
    Success,

    /// <summary>No such product.</summary>
    NotFound,

    /// <summary>
    /// The catalog itself is malformed — a cycle, a walk past the depth limit, or a made component
    /// whose maker is missing. Distinct from <see cref="NotFound"/> because the product is right
    /// there; it is what it is built from that cannot be resolved.
    /// </summary>
    NotExplodable
}

public class GetProductBomResponse
{
    public GetProductBomOutcome Outcome { get; init; }
    public ProductBomDto? Bom { get; init; }
    public string? Error { get; init; }
}
