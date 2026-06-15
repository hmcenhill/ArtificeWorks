using Microsoft.AspNetCore.Mvc;

using OrderProcessing.Application.Commands;
using OrderProcessing.Application.Handlers;

namespace OrderProcessing.Api.Controllers;

[ApiController]
[Route("products")]
public class ProductController : ControllerBase
{
    private readonly ProductHandler _productHandler;

    public ProductController(ProductHandler productHandler)
    {
        _productHandler = productHandler;
    }

    [HttpGet("{productId}")]
    public async Task<ActionResult<GetProductResponse>> Get(string productId)
    {
        var response = await _productHandler.GetProduct(productId);
        if (response.IsSuccess)
        {
            return Ok(response.Product);
        }
        return NotFound(response.Error);
    }

    [HttpPost]
    public async Task<ActionResult<CreateProductResponse>> Create([FromBody] CreateProductRequest request)
    {
        var response = await _productHandler.CreateProduct(request);
        if (response.IsSuccess)
        {
            return Created($"/products/{response.Product!.ItemId}", response.Product);
        }
        return BadRequest(response.Error);
    }
}