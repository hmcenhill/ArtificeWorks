namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// A raw input material the factory consumes to build a <see cref="Product"/> — a chassis,
/// a power core, a sensor array. Deliberately a different concept from <see cref="Product"/>
/// (a finished automaton) and from <see cref="StockKeepingUnit"/> (a serialized finished
/// unit): components are fungible and counted, not serialized.
/// <para>
/// On-hand inventory is a single quantity on the component itself — no warehouse/location
/// aggregate and no reserved-vs-available split yet. Reservation simply draws
/// <see cref="OnHand"/> down, which keeps the concurrency story (5.3) about exactly one
/// number.
/// </para>
/// <para>
/// 13.2 makes a component optionally <em>manufactured</em>: <see cref="MakeProductId"/> names the
/// product that builds it. That one nullable column is the whole of the multi-level BOM — the
/// flat <c>bom_lines</c> table is untouched, and a made component that is on the shelf is picked
/// by exactly the same conditional decrement as a bought one.
/// </para>
/// </summary>
public class Component
{
    public string ComponentId { get; }
    public string ComponentName { get; }

    /// <summary>
    /// The <see cref="Product"/> that manufactures this component, or <c>null</c> for a bought
    /// part — which is the large majority.
    /// <para>
    /// <strong>The link lives here rather than on <see cref="BomLine"/></strong> because "made or
    /// bought?" is a property of the part, not of one product's use of it: the same control stack
    /// is built the same way whoever calls for it. On the line, two products could disagree about
    /// how the same component comes to exist.
    /// </para>
    /// </summary>
    public string? MakeProductId { get; private set; }

    /// <summary>True when the factory builds this component rather than buying it in.</summary>
    public bool IsMade => MakeProductId is not null;

    /// <summary>Units physically on the shelf and not yet consumed by a reservation.</summary>
    public uint OnHand { get; private set; }

    /// <summary>
    /// How much of this component the factory starts with — the level 10.4's world sweep restocks
    /// to.
    /// <para>
    /// <strong>Seed levels became data rather than staying in the seeder's arrays</strong> because
    /// restock needs a target, and re-deriving it at runtime from a static field on a class whose
    /// job is first-run setup couples a background sweep to <c>CatalogSeeder</c>. Written once at
    /// creation; there is then exactly one definition of "how much this factory starts with".
    /// </para>
    /// </summary>
    public uint SeedOnHand { get; private set; }

    private Component() { }

    public Component(string componentId, string componentName, uint onHand = 0, string? makeProductId = null)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            throw new ArgumentException("Component id is required.", nameof(componentId));
        }

        ComponentId = componentId;
        ComponentName = componentName;
        OnHand = onHand;
        SeedOnHand = onHand;

        if (makeProductId is not null)
        {
            MarkMadeBy(makeProductId);
        }
    }

    /// <summary>
    /// Records that this component is manufactured by <paramref name="productId"/>.
    /// <para>
    /// The guard here is the cheap one — a component cannot name itself as its own maker. It
    /// catches a copy-paste, not a cycle: a real cycle needs two individually-legal edits (P makes
    /// C, and P's BOM eventually reaches C), so it cannot be caught by any single aggregate. The
    /// guard that matters is <see cref="BomExplosion"/>'s, which walks the whole tree.
    /// </para>
    /// </summary>
    public void MarkMadeBy(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
        {
            throw new ArgumentException("A making product id is required.", nameof(productId));
        }

        if (productId == ComponentId)
        {
            throw new InvalidOperationException(
                $"Component {ComponentId} cannot be manufactured by itself.");
        }

        MakeProductId = productId;
    }

    /// <summary>
    /// In-memory decrement used by the domain and unit tests. The persisted reservation path
    /// does <em>not</em> rely on this read-modify-write — it issues an atomic conditional
    /// UPDATE instead (see the reservation repository), because two workers racing on the
    /// same component would otherwise both read the same stale on-hand and oversell.
    /// </summary>
    public bool TryConsume(uint quantity)
    {
        if (quantity > OnHand)
        {
            return false;
        }
        OnHand -= quantity;
        return true;
    }

    public void Restock(uint quantity) => OnHand += quantity;
}
