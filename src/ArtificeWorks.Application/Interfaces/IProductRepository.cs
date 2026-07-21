using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Interfaces;

public interface IProductRepository
{
    Task<Product?> Get(string id);

    /// <summary>
    /// The product with its bill of materials (and each line's component) loaded — what the
    /// picking workflow needs to expand demand.
    /// </summary>
    Task<Product?> GetWithBom(string id);

    Task<Product> Add(Product product);
}
