using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Data;

public class ProductDto
{
    public string ItemId { get; }
    public string ItemName { get; }


    public ProductDto(Product product)
    {
        ItemId = product.ItemId;
        ItemName = product.ItemName;
    }
}