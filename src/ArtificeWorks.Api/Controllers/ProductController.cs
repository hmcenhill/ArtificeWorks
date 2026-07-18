using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Handlers;

namespace ArtificeWorks.Api.Controllers;

[ApiController]
[Route("products")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class ProductController(ProductHandler productHandler) : ApiControllerBase
{
    private readonly ProductHandler _productHandler = productHandler;

    [HttpGet("{productId}")]
    [ProducesResponseType<GetProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GetProductResponse>> Get(string productId)
    {
        var response = await _productHandler.GetProduct(productId);
        return response.IsSuccess
            ? Ok(response.Product)
            : Problem(StatusCodes.Status404NotFound, ProblemCodes.ProductNotFound, response.Error!);
    }

    [HttpPost]
    [ProducesResponseType<CreateProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateProductResponse>> Create([FromBody] CreateProductRequest request)
    {
        var response = await _productHandler.CreateProduct(request);
        return response.Outcome switch
        {
            CreateProductOutcome.Success => Created($"/products/{response.Product!.ItemId}", response.Product),
            // A product with this id already exists — a conflicting duplicate (409).
            CreateProductOutcome.AlreadyExists
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.ProductAlreadyExists, response.Error!),
            _ => Problem(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError,
                "The product could not be saved.")
        };
    }
}
