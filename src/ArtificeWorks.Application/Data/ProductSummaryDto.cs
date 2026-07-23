using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// The catalog row the create form reads (11.3): just enough to pick a product by name and send
/// its id. The full <see cref="ProductDto"/> (with its bill of materials) is what
/// <c>GET /products/{id}</c> returns; a picker does not need it.
/// </summary>
public sealed record ProductSummaryDto(string ItemId, string ItemName)
{
    public ProductSummaryDto(Product product) : this(product.ItemId, product.ItemName) { }
}
