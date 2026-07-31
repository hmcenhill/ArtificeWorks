using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// A product's bill of materials exploded to its bought leaves — what <c>GET /products/{id}/bom</c>
/// returns (13.2), and what 13.4's commonality view renders.
/// <para>
/// <strong>A sub-resource rather than more fields on <see cref="ProductDto"/></strong>: the flat
/// BOM on <c>GET /products/{id}</c> is what the create form reads, and a whole exploded tree
/// arriving in that payload would be a bigger response for every caller that only wanted names.
/// </para>
/// <para>
/// Shaped for a client that draws the tree without a second call: every node carries its children
/// inline, along with the made/bought flag and on-hand stock a reader needs to see where the tree
/// is thin.
/// </para>
/// </summary>
public sealed class ProductBomDto
{
    public string ItemId { get; }
    public string ItemName { get; }

    /// <summary>The quantity every extended figure below was scaled by.</summary>
    public uint OrderQty { get; }

    /// <summary>The product's own BOM lines, each with its sub-tree.</summary>
    public IReadOnlyList<BomNodeDto> Tree { get; }

    /// <summary>
    /// One row per bought component, totalled across every path that reaches it. Not what picking
    /// draws — picking takes the one-level demand, because a made component in stock is picked
    /// rather than exploded — but what the order would cost with nothing on the shelf.
    /// </summary>
    public IReadOnlyList<LeafDemandDto> LeafDemand { get; }

    public ProductBomDto(BomExplosionResult explosion)
    {
        ItemId = explosion.ProductId;
        ItemName = explosion.ProductName;
        OrderQty = explosion.OrderQty;
        Tree = explosion.Tree.Select(node => new BomNodeDto(node)).ToList();
        LeafDemand = explosion.LeafDemand
            .Select(demand => new LeafDemandDto(demand.ComponentId, demand.ComponentName, demand.Quantity))
            .ToList();
    }
}

/// <summary>One node of the exploded tree; see <see cref="BomNode"/> for what each figure means.</summary>
public sealed class BomNodeDto
{
    public string ComponentId { get; }
    public string ComponentName { get; }
    public uint QtyPerParent { get; }
    public uint ExtendedQty { get; }
    public int Depth { get; }

    /// <summary>True when the factory builds this component; <see cref="MakeProductId"/> names the product.</summary>
    public bool IsMade { get; }
    public string? MakeProductId { get; }

    public uint OnHand { get; }
    public IReadOnlyList<BomNodeDto> Children { get; }

    public BomNodeDto(BomNode node)
    {
        ComponentId = node.ComponentId;
        ComponentName = node.ComponentName;
        QtyPerParent = node.QtyPerParent;
        ExtendedQty = node.ExtendedQty;
        Depth = node.Depth;
        IsMade = node.IsMade;
        MakeProductId = node.MakeProductId;
        OnHand = node.OnHand;
        Children = node.Children.Select(child => new BomNodeDto(child)).ToList();
    }
}

/// <summary>Total demand for one bought component across the whole exploded tree.</summary>
public sealed record LeafDemandDto(string ComponentId, string ComponentName, uint Quantity);
