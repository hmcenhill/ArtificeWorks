using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Interfaces;

public interface IProductRepository
{
    Task<Product?> Get(string id);

    /// <summary>
    /// The catalog as a slim list — every product's id and name, in catalog order. What the
    /// dashboard's create form reads (11.3) to offer the three lines; no BOM, because a template
    /// picker chooses a product, it does not expand one.
    /// </summary>
    Task<IReadOnlyList<Product>> List();

    /// <summary>
    /// The product with its bill of materials (and each line's component) loaded — what the
    /// picking workflow needs to expand demand.
    /// </summary>
    Task<Product?> GetWithBom(string id);

    Task<Product> Add(Product product);
}
