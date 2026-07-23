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

    public async Task<IReadOnlyList<Product>> List()
    {
        // No tracking and no BOM: the board's create form only needs id + name. Catalog order is
        // by id, which keeps the three seeded lines stable between requests.
        return await _context.Products
            .AsNoTracking()
            .OrderBy(p => p.ItemId)
            .ToListAsync();
    }

    public async Task<Product?> GetWithBom(string id)
    {
        // No-tracking on purpose: the picking workflow only reads the BOM, and its reservation
        // path decrements on-hand with raw SQL — a tracked Component would immediately be a
        // stale copy of a row the database has already moved on from.
        return await _context.Products
            .AsNoTracking()
            .Include(p => p.BillOfMaterials)
                .ThenInclude(line => line.Component)
            .FirstOrDefaultAsync(p => p.ItemId == id);
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