using OrderProcessing.Domain.Models.Materials;

namespace OrderProcessing.Application.Interfaces;

public interface IProductRepository
{
    Task<Product?> Get(string id);
    Task<Product> Add(Product product);
}