using OrderProcessing.Application.Data;

namespace OrderProcessing.Application.Commands;

public class CreateProductResponse
{
    public bool IsSuccess { get; set; }
    public ProductDto? Product { get; set; }
    public string? Error { get; set; }
}