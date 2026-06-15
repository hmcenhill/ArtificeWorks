using Microsoft.EntityFrameworkCore;

using OrderProcessing.Application.Interfaces;
using OrderProcessing.Domain.Models.Materials;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Infrastructure.Data;

public class ProductRepository : IProductRepository
{
    private readonly WorkOrderProcessingDbContext _context;

    public ProductRepository(WorkOrderProcessingDbContext context)
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