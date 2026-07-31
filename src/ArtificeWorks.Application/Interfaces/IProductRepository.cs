using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Interfaces;

public interface IProductRepository
{
    Task<Product?> Get(string id);

    /// <summary>
    /// The catalog as a slim list — every product's id and name, in catalog order. What the
    /// dashboard's create form reads (11.3) to offer the three lines; no BOM, because a template
    /// picker chooses a product, it does not expand one.
    /// </summary>
    Task<IReadOnlyList<Product>> List();

    /// <summary>
    /// The product with its bill of materials (and each line's component) loaded — what the
    /// picking workflow needs to expand demand.
    /// </summary>
    Task<Product?> GetWithBom(string id);

    /// <summary>
    /// The whole catalog with every BOM loaded — what <c>BomExplosion</c> walks (13.2), since a
    /// made component can point at any product and following the tree a level at a time would be
    /// a query per node.
    /// <para>
    /// Loading all of it is a deliberate call at this scale: the catalog is a handful of seeded
    /// rows that change only when someone edits the catalog, and one query beats N+1 against the
    /// same tiny table. If the catalog ever grows to the point where this is wrong, the fix is a
    /// recursive CTE, not a loop of round-trips.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Product>> ListWithBoms();

    /// <summary>
    /// The ids of products that exist to build a component — i.e. every product some component
    /// names as its maker (13.2). What tells a catalog listing an automaton from a sub-assembly
    /// without loading a BOM.
    /// </summary>
    Task<IReadOnlyList<string>> ListSubAssemblyProductIds();

    Task<Product> Add(Product product);
}
