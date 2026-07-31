using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Handlers;

public class ProductHandler
{
    private readonly IProductRepository _productRepository;

    public ProductHandler(IProductRepository productRepository)
    {
        _productRepository = productRepository;
    }

    /// <summary>The catalog as slim summary rows, in catalog order — the create form's source (11.3).</summary>
    public async Task<IReadOnlyList<ProductSummaryDto>> ListProducts()
    {
        var products = await _productRepository.List();

        // 13.2. Sub-assemblies are products too, so they appear here — but a customer does not
        // order a bare core casing, and the create form filters on this flag. The second query is
        // a handful of rows; the alternative is a caller that cannot tell an automaton from the
        // thing inside it.
        var subAssemblyIds = (await _productRepository.ListSubAssemblyProductIds()).ToHashSet();

        return products
            .Select(p => new ProductSummaryDto(p, subAssemblyIds.Contains(p.ItemId)))
            .ToList();
    }

    /// <summary>
    /// The product's bill of materials exploded to its bought leaves (13.2) —
    /// <c>GET /products/{id}/bom</c>.
    /// </summary>
    /// <param name="orderQty">Finished units to extend every quantity for; 1 reads as "per unit".</param>
    public async Task<GetProductBomResponse> GetProductBom(string productId, uint orderQty)
    {
        // The whole catalog, because a made component may point at any product in it — see
        // IProductRepository.ListWithBoms for why that is one query rather than a walk.
        var catalog = await _productRepository.ListWithBoms();
        var product = catalog.FirstOrDefault(p => p.ItemId == productId);

        if (product is null)
        {
            return new GetProductBomResponse
            {
                Outcome = GetProductBomOutcome.NotFound,
                Error = $"No product found with id: {productId}"
            };
        }

        try
        {
            var explosion = BomExplosion.Explode(
                product, orderQty, catalog.ToDictionary(p => p.ItemId, StringComparer.Ordinal));

            return new GetProductBomResponse
            {
                Outcome = GetProductBomOutcome.Success,
                Bom = new ProductBomDto(explosion)
            };
        }
        catch (BomExplosionException e)
        {
            // A cycle, a walk past the depth cap, or a missing maker. The message names the
            // offending component and the path to it, and it is safe to surface: the catalog is
            // public data, and a caller staring at a broken BOM needs to know which part is broken.
            return new GetProductBomResponse
            {
                Outcome = GetProductBomOutcome.NotExplodable,
                Error = e.Message
            };
        }
    }

    public async Task<GetProductResponse> GetProduct(string productId)
    {
        // Read with the BOM so GET /products/{id} shows what the product is made of.
        var product = await _productRepository.GetWithBom(productId);
        var errors = "";
        if (product is null)
        {
            errors = $"No product found with id: {productId}";
        }
        else
        {
            return new GetProductResponse
            {
                IsSuccess = true,
                Product = new ProductDto(product)
            };
        }
        return new GetProductResponse
        {
            IsSuccess = false,
            Error = errors
        };
    }

    public async Task<CreateProductResponse> CreateProduct(CreateProductRequest request)
    {
        var existingProduct = await _productRepository.Get(request.ProductId);
        if (existingProduct is not null)
        {
            return new CreateProductResponse
            {
                Outcome = CreateProductOutcome.AlreadyExists,
                Error = $"Product with id: {request.ProductId} already exists."
            };
        }

        var newProduct = new Product(request.ProductId, request.ProductName);
        try
        {
            var savedProduct = await _productRepository.Add(newProduct);
            if (savedProduct is not null)
            {
                return new CreateProductResponse
                {
                    Outcome = CreateProductOutcome.Success,
                    Product = new ProductDto(newProduct)
                };
            }
            return new CreateProductResponse
            {
                Outcome = CreateProductOutcome.Error,
                Error = "Save action returned no response"
            };
        }
        catch (Exception e)
        {
            return new CreateProductResponse
            {
                Outcome = CreateProductOutcome.Error,
                Error = e.Message
            };
        }
    }
}
