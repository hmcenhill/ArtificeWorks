using OrderProcessing.Application.Data;

namespace OrderProcessing.Application.Commands;

public class GetProductResponse
{
    public bool IsSuccess { get; init; }
    public ProductDto? Product { get; init; }
    public string? Error { get; init; }
}
