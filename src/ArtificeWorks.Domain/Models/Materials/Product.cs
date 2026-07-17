namespace ArtificeWorks.Domain.Models.Materials;

public class Product
{
    public string ItemId { get; }
    public string ItemName { get; }

    private Product() { }
    public Product(string ItemId, string ItemName)
    {
        this.ItemId = ItemId;
        this.ItemName = ItemName;
    }

}