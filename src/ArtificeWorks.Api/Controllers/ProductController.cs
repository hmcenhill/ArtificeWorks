using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Handlers;

namespace ArtificeWorks.Api.Controllers;

[ApiController]
[Route("products")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class ProductController(ProductHandler productHandler) : ApiControllerBase
{
    private readonly ProductHandler _productHandler = productHandler;

    /// <summary>
    /// The catalog as a slim list — the three product lines by id and name, in catalog order.
    /// The dashboard's create form (11.3) reads this to offer templates; a template picker chooses
    /// a product, so this deliberately omits the bill of materials that <see cref="Get"/> carries.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProductSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductSummaryDto>>> List()
        => Ok(await _productHandler.ListProducts());

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

    /// <summary>
    /// The product's bill of materials exploded to its bought leaves (13.2): the tree, with each
    /// node's made/bought flag, extended quantity and on-hand stock, plus the aggregated leaf
    /// demand underneath it.
    /// <para>
    /// A sub-resource rather than more fields on <see cref="Get"/>, because the flat BOM there is
    /// what the create form reads and it should not grow a whole tree for every caller that wanted
    /// names. Everything a client needs to draw the tree is in one response.
    /// </para>
    /// </summary>
    /// <param name="qty">Finished units to extend every quantity for. Defaults to 1 — per unit.</param>
    [HttpGet("{productId}/bom")]
    [ProducesResponseType<ProductBomDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductBomDto>> GetBom(string productId, [FromQuery] uint qty = 1)
    {
        if (qty == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationFailed,
                "Quantity must be greater than 0.");
        }

        var response = await _productHandler.GetProductBom(productId, qty);
        return response.Outcome switch
        {
            GetProductBomOutcome.Success => Ok(response.Bom),
            GetProductBomOutcome.NotFound
                => Problem(StatusCodes.Status404NotFound, ProblemCodes.ProductNotFound, response.Error!),
            // The catalog is cyclic, too deep, or missing a maker — the request was fine, so this
            // is a 409 about the state of the data rather than a 400 about the caller.
            GetProductBomOutcome.NotExplodable
                => Problem(StatusCodes.Status409Conflict, ProblemCodes.BomNotExplodable, response.Error!),
            _ => Problem(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError,
                "The bill of materials could not be read.")
        };
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
