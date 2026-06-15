using OrderProcessing.Application.Commands;
using OrderProcessing.Application.Data;
using OrderProcessing.Application.Interfaces;
using OrderProcessing.Domain.Models.Materials;

namespace OrderProcessing.Application.Handlers;

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
        var errors = string.Empty;
        var existingProduct = await _productRepository.Get(request.ProductId);
        if (existingProduct is not null)
        {
            errors = $"Error: Product with id: {request.ProductId} already exists.";
        }
        else
        {
            var newProduct = new Product(request.ProductId, request.ProductName);
            try
            {
                var savedProduct = await _productRepository.Add(newProduct);
                if (savedProduct is not null)
                {
                    return new CreateProductResponse
                    {
                        IsSuccess = true,
                        Product = new ProductDto(newProduct)
                    };
                }
                errors = "Save action returned no response";
            }
            catch (Exception e)
            {
                errors = e.Message;
            }
        }
        return new CreateProductResponse
        {
            IsSuccess = false,
            Error = errors
        };
    }
}
