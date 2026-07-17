using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Interfaces;

public interface IProductRepository
{
    Task<Product?> Get(string id);
    Task<Product> Add(Product product);
}