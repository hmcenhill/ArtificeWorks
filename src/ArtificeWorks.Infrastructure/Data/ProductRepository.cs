using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Persistence;

namespace ArtificeWorks.Infrastructure.Data;

public class ProductRepository : IProductRepository
{
    private readonly ArtificeWorksDbContext _context;

    public ProductRepository(ArtificeWorksDbContext context)
    {
        _context = context;
    }

    public async Task<Product?> Get(string id)
    {
        var product = await _context.Products.FirstOrDefaultAsync(p => p.ItemId == id);
        return product;
    }

    public async Task<Product> Add(Product product)
    {
        var existing = await Get(product.ItemId);
        if (existing is not null)
        {
            throw new Exception($"Product with id: {product.ItemId} already exists");
        }

        var createdProduct = await _context.Products.AddAsync(product);
        await _context.SaveChangesAsync();
        return createdProduct.Entity;
    }
}