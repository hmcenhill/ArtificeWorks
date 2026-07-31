using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Data;

public class ProductDto
{
    public string ItemId { get; }
    public string ItemName { get; }

    /// <summary>
    /// The product's flat bill of materials — one level, which is also exactly what picking draws.
    /// The exploded tree underneath it is a separate resource, <c>GET /products/{id}/bom</c>
    /// (13.2), so this payload stays the small thing the create form reads.
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

    /// <summary>
    /// True when the factory builds this component rather than buying it in (13.2);
    /// <see cref="MakeProductId"/> names the product that does. One flat level down is all this
    /// says — follow the sub-resource for the rest of the tree.
    /// </summary>
    public bool IsMade { get; }
    public string? MakeProductId { get; }

    public BomLineDto(BomLine line)
    {
        ComponentId = line.Component.ComponentId;
        ComponentName = line.Component.ComponentName;
        QtyPerUnit = line.QtyPerUnit;
        OnHand = line.Component.OnHand;
        IsMade = line.Component.IsMade;
        MakeProductId = line.Component.MakeProductId;
    }
}
