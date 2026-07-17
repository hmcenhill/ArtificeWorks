namespace ArtificeWorks.Domain.Models.Materials;

public class StockKeepingUnit
{
    public Product Product { get; }
    public Guid SerialNumber { get; }

    private StockKeepingUnit() { }

    public StockKeepingUnit(Product product)
    {
        Product = product;
        SerialNumber = Guid.NewGuid();
    }
}
