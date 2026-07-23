using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;

namespace ArtificeWorks.IntegrationTests;

public class ProductApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ProductApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    // ProductDto is serialize-only (no parameterless ctor), so tests read the
    // wire shape into their own record rather than the production DTO.
    private sealed record ProductResponse(string ItemId, string ItemName);

    [Fact]
    public async Task CreateProduct_CreatesAndReturnsCreatedProduct()
    {
        // Act
        var response = await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Prod-Create-001",
            ProductName = "Clockwork Sparrow"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.NotNull(product);
        Assert.Equal("Prod-Create-001", product.ItemId);
        Assert.Equal("Clockwork Sparrow", product.ItemName);
    }

    [Fact]
    public async Task CreateProduct_Duplicate_ReturnsConflictWithReason()
    {
        // Arrange — first create succeeds
        var request = new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Prod-Dupe-001",
            ProductName = "Clockwork Owl"
        };
        var first = await _fixture.Client.PostAsJsonAsync("/products", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Act — the same id again is a conflicting duplicate
        var second = await _fixture.Client.PostAsJsonAsync("/products", request);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("product_already_exists", await second.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task GetProduct_ReturnsExistingProduct()
    {
        // Arrange
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Prod-Get-001",
            ProductName = "Clockwork Fox"
        });

        // Act
        var response = await _fixture.Client.GetAsync("/products/Prod-Get-001");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.NotNull(product);
        Assert.Equal("Prod-Get-001", product.ItemId);
        Assert.Equal("Clockwork Fox", product.ItemName);
    }

    [Fact]
    public async Task GetProduct_MissingProduct_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/products/Does-Not-Exist");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("product_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task ListProducts_ReturnsCreatedProducts()
    {
        // Arrange — two products the list must include (alongside whatever the catalog seeded).
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Prod-List-001",
            ProductName = "Clockwork Hare"
        });
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "John Tester",
            ProductId = "Prod-List-002",
            ProductName = "Clockwork Stag"
        });

        // Act
        var response = await _fixture.Client.GetAsync("/products");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var products = await response.Content.ReadFromJsonAsync<List<ProductResponse>>();
        Assert.NotNull(products);
        Assert.Contains(products, p => p.ItemId == "Prod-List-001" && p.ItemName == "Clockwork Hare");
        Assert.Contains(products, p => p.ItemId == "Prod-List-002" && p.ItemName == "Clockwork Stag");
    }
}
