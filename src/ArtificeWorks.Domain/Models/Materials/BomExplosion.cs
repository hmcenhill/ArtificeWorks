namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// Walks a product's bill of materials to its bought leaves, following every component that is
/// itself manufactured (<see cref="Component.MakeProductId"/>) into the BOM of the product that
/// makes it.
/// <para>
/// <strong>This is not planning.</strong> It answers "what is this made of?", not "what should we
/// buy?" — it never subtracts on-hand and never proposes an order. On-hand travels on each node
/// only so a reader can see where the tree is thin. Picking still uses the one-level
/// <see cref="Product.ComputeDemand"/>; a made component with stock is drawn off the shelf, not
/// exploded.
/// </para>
/// <para>
/// Pure domain, deliberately: the caller supplies the catalog it has already loaded, so the whole
/// recursion — including the cycle refusal, which is the piece where a bug is a runaway rather
/// than a bad demo — is unit-testable without a database.
/// </para>
/// </summary>
public static class BomExplosion
{
    /// <summary>
    /// How many levels below the finished product the walk will go before refusing.
    /// <para>
    /// The seeded catalog is three levels deep (automaton → power core → core casing → its parts),
    /// so five is real headroom rather than a limit anyone is expected to hit. It exists for the
    /// case cycle detection cannot see: not a loop, but a chain long enough that something has
    /// gone wrong in the data. A cap is cheap; an unbounded walk over a catalog a visitor can edit
    /// is not.
    /// </para>
    /// </summary>
    public const int MaxDepth = 5;

    /// <summary>
    /// Explodes <paramref name="product"/> for <paramref name="orderQty"/> finished units.
    /// </summary>
    /// <param name="product">The root product, with its BOM lines and their components loaded.</param>
    /// <param name="orderQty">Finished units ordered; every extended quantity is scaled by it.</param>
    /// <param name="catalog">
    /// Every product a made component might point at, keyed by product id, each with its BOM
    /// loaded. A made component whose product is absent is an error, not a leaf — silently
    /// treating it as bought would understate demand.
    /// </param>
    /// <exception cref="BomExplosionException">
    /// The tree contains a cycle, exceeds <see cref="MaxDepth"/>, or names a product the catalog
    /// does not contain.
    /// </exception>
    public static BomExplosionResult Explode(
        Product product,
        uint orderQty,
        IReadOnlyDictionary<string, Product> catalog)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(catalog);

        if (orderQty == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderQty), "Order quantity must be greater than 0.");
        }

        var leafDemand = new Dictionary<string, (string Name, uint Quantity)>(StringComparer.Ordinal);

        // The path is the products currently being expanded, root first. Membership means "we are
        // already inside this product", which is exactly a cycle — and it is a *path*, not a
        // visited set, so a component legitimately reached down two different branches (a diamond)
        // is expanded on both and is not mistaken for a loop.
        var path = new List<string> { product.ItemId };

        var tree = Walk(product, orderQty, depth: 1, catalog, path, leafDemand);

        return new BomExplosionResult(
            product.ItemId,
            product.ItemName,
            orderQty,
            tree,
            leafDemand
                .Select(entry => new ComponentDemand(entry.Key, entry.Value.Name, entry.Value.Quantity))
                // The same StringComparer.Ordinal rule ComputeDemand follows, so a caller can
                // compare the two answers line for line.
                .OrderBy(demand => demand.ComponentId, StringComparer.Ordinal)
                .ToList());
    }

    private static IReadOnlyList<BomNode> Walk(
        Product product,
        uint parentExtendedQty,
        int depth,
        IReadOnlyDictionary<string, Product> catalog,
        List<string> path,
        Dictionary<string, (string Name, uint Quantity)> leafDemand)
    {
        if (depth > MaxDepth)
        {
            throw new BomExplosionException(
                $"Bill of materials for {path[0]} is deeper than the {MaxDepth}-level limit " +
                $"(reached via {string.Join(" → ", path)}).");
        }

        var nodes = new List<BomNode>();

        foreach (var line in product.BillOfMaterials
            .OrderBy(line => line.Component.ComponentId, StringComparer.Ordinal))
        {
            var component = line.Component;
            var extendedQty = line.QtyPerUnit * parentExtendedQty;

            IReadOnlyList<BomNode> children = [];

            if (component.MakeProductId is { } makeProductId)
            {
                if (path.Contains(makeProductId, StringComparer.Ordinal))
                {
                    throw new BomExplosionException(
                        $"Component {component.ComponentId} is made by product {makeProductId}, " +
                        $"which is already being built further up the tree " +
                        $"({string.Join(" → ", path)} → {makeProductId}). The bill of materials is cyclic.");
                }

                if (!catalog.TryGetValue(makeProductId, out var maker))
                {
                    throw new BomExplosionException(
                        $"Component {component.ComponentId} is made by product {makeProductId}, " +
                        "which is not in the catalog.");
                }

                path.Add(makeProductId);
                children = Walk(maker, extendedQty, depth + 1, catalog, path, leafDemand);
                path.RemoveAt(path.Count - 1);
            }
            else
            {
                // Only bought parts land in leaf demand, and a component reached by two paths
                // accumulates into the one row — this is the number a buyer would act on.
                var running = leafDemand.TryGetValue(component.ComponentId, out var existing)
                    ? existing.Quantity
                    : 0;
                leafDemand[component.ComponentId] = (component.ComponentName, running + extendedQty);
            }

            nodes.Add(new BomNode(
                component.ComponentId,
                component.ComponentName,
                line.QtyPerUnit,
                extendedQty,
                depth,
                component.MakeProductId,
                component.OnHand,
                children));
        }

        return nodes;
    }
}

/// <summary>
/// One component in an exploded bill of materials, with the sub-tree it expands into when the
/// factory makes it rather than buys it.
/// </summary>
/// <param name="ComponentId">The component this node stands for.</param>
/// <param name="ComponentName">Its display name.</param>
/// <param name="QtyPerParent">How many the immediate parent calls for — the raw BOM line quantity.</param>
/// <param name="ExtendedQty">How many the whole order needs: this line's quantity multiplied all
/// the way up to the root.</param>
/// <param name="Depth">Levels below the finished product; the root's own BOM lines are depth 1.</param>
/// <param name="MakeProductId">The product that builds it, or <c>null</c> when it is bought in.</param>
/// <param name="OnHand">Factory-wide stock, so a thin branch is visible without a second call.</param>
/// <param name="Children">Its own BOM, exploded — empty for a bought part.</param>
public sealed record BomNode(
    string ComponentId,
    string ComponentName,
    uint QtyPerParent,
    uint ExtendedQty,
    int Depth,
    string? MakeProductId,
    uint OnHand,
    IReadOnlyList<BomNode> Children)
{
    /// <summary>True when the factory builds this component rather than buying it in.</summary>
    public bool IsMade => MakeProductId is not null;
}

/// <summary>
/// The two shapes a caller of <see cref="BomExplosion"/> wants: the tree to draw, and the
/// aggregated bought-part demand underneath it.
/// </summary>
/// <param name="ProductId">The exploded product.</param>
/// <param name="ProductName">Its display name.</param>
/// <param name="OrderQty">The quantity every extended figure was scaled by.</param>
/// <param name="Tree">The product's BOM lines, each carrying its own sub-tree.</param>
/// <param name="LeafDemand">
/// One row per bought component, totalled across every path that reaches it. Note this is
/// <em>not</em> what picking draws — picking takes the one-level
/// <see cref="Product.ComputeDemand"/>, because a made component in stock is picked, not
/// exploded. Leaf demand is what the order would cost if nothing were on the shelf at all.
/// </param>
public sealed record BomExplosionResult(
    string ProductId,
    string ProductName,
    uint OrderQty,
    IReadOnlyList<BomNode> Tree,
    IReadOnlyList<ComponentDemand> LeafDemand);

/// <summary>
/// A bill of materials that cannot be exploded: cyclic, deeper than
/// <see cref="BomExplosion.MaxDepth"/>, or naming a product that is not in the catalog. All three
/// are structural faults in the catalog data rather than anything a caller did wrong.
/// </summary>
public class BomExplosionException : Exception
{
    public BomExplosionException(string message) : base(message) { }
}
