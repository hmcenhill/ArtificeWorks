using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Data;

public class ProductDto
{
    public string ItemId { get; }
    public string ItemName { get; }

    /// <summary>
    /// The product's flat bill of materials. Enough surface to eyeball the seeded catalog and
    /// confirm the shared-platform overlap; a full BOM API (editing, commonality reporting)
    /// belongs to the dashboard epic.
    /// </summary>
    public IReadOnlyList<BomLineDto> BillOfMaterials { get; }

    public ProductDto(Product product)
    {
        ItemId = product.ItemId;
        ItemName = product.ItemName;
        BillOfMaterials = product.BillOfMaterials
            .Select(line => new BomLineDto(line))
            .ToList();
    }
}

public class BomLineDto
{
    public string ComponentId { get; }
    public string ComponentName { get; }
    public uint QtyPerUnit { get; }

    /// <summary>Factory-wide on-hand stock for this component, so a shortage is visible from the catalog.</summary>
    public uint OnHand { get; }

    public BomLineDto(BomLine line)
    {
        ComponentId = line.Component.ComponentId;
        ComponentName = line.Component.ComponentName;
        QtyPerUnit = line.QtyPerUnit;
        OnHand = line.Component.OnHand;
    }
}
