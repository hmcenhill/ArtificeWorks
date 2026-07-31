using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// The catalog row the create form reads (11.3): just enough to pick a product by name and send
/// its id. The full <see cref="ProductDto"/> (with its bill of materials) is what
/// <c>GET /products/{id}</c> returns; a picker does not need it.
/// </summary>
/// <param name="IsSubAssembly">
/// True when this product exists to build a component rather than to be sold (13.2). The catalog
/// lists both — a sub-assembly is an ordinary product and the pipeline treats it as one — but a
/// customer-facing picker filters on this, because "Core Casing Assembly" is not an automaton
/// anyone orders.
/// </param>
public sealed record ProductSummaryDto(string ItemId, string ItemName, bool IsSubAssembly)
{
    public ProductSummaryDto(Product product, bool isSubAssembly)
        : this(product.ItemId, product.ItemName, isSubAssembly) { }
}
