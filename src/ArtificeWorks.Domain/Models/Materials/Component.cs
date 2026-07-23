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
/// number. Epic 13's multi-level BOM is where a component may itself become manufactured.
/// </para>
/// </summary>
public class Component
{
    public string ComponentId { get; }
    public string ComponentName { get; }

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

    public Component(string componentId, string componentName, uint onHand = 0)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            throw new ArgumentException("Component id is required.", nameof(componentId));
        }

        ComponentId = componentId;
        ComponentName = componentName;
        OnHand = onHand;
        SeedOnHand = onHand;
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
