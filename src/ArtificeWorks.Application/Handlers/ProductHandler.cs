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

    public async Task<GetProductResponse> GetProduct(string productId)
    {
        var product = await _productRepository.Get(productId);
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
