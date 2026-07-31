namespace ArtificeWorks.Domain.Models.Materials;

public class Product
{
    public string ItemId { get; }
    public string ItemName { get; }

    /// <summary>
    /// The product's flat bill of materials — the components (and per-unit quantities) one
    /// finished automaton is built from. Empty for a product whose BOM hasn't been defined,
    /// which the picking workflow treats as "nothing to reserve".
    /// </summary>
    public IReadOnlyList<BomLine> BillOfMaterials => _billOfMaterials.AsReadOnly();
    private readonly List<BomLine> _billOfMaterials = new();

    private Product() { }
    public Product(string ItemId, string ItemName)
    {
        this.ItemId = ItemId;
        this.ItemName = ItemName;
    }

    /// <summary>
    /// Adds a BOM line, or raises the quantity of an existing one for the same component
    /// (idempotent-friendly, so the catalog seeder can run repeatedly without duplicating
    /// lines — it checks the component isn't already present).
    /// </summary>
    public BomLine AddBomLine(Component component, uint qtyPerUnit)
    {
        if (_billOfMaterials.Any(line => line.Component.ComponentId == component.ComponentId))
        {
            throw new InvalidOperationException(
                $"Product {ItemId} already has a BOM line for component {component.ComponentId}.");
        }

        // 13.2. The one-step cycle, and the only one an aggregate can see on its own: this product
        // is what builds the component, so consuming it would mean building it out of itself.
        // Anything longer than one step is BomExplosion's job.
        if (component.MakeProductId == ItemId)
        {
            throw new InvalidOperationException(
                $"Product {ItemId} cannot consume component {component.ComponentId}, which it manufactures.");
        }

        var bomLine = new BomLine(this, component, qtyPerUnit);
        _billOfMaterials.Add(bomLine);
        return bomLine;
    }

    /// <summary>
    /// Expands the BOM into the concrete component demand for <paramref name="orderQty"/>
    /// finished units. This is the whole of the "what does this order need?" rule and it
    /// lives in the domain deliberately, so the picking workflow can be reasoned about (and
    /// unit-tested) without a database.
    /// <para>
    /// <strong>One level, on purpose — 13.2 did not change this.</strong> It answers "what does
    /// picking draw off the shelf for this product?", and a manufactured component sitting in
    /// stock is drawn exactly like a bought one. Recursion belongs to
    /// <see cref="BomExplosion"/>, which is a reporting and planning concern; turning picking
    /// into a recursive planner would make it MRP.
    /// </para>
    /// </summary>
    public IReadOnlyList<ComponentDemand> ComputeDemand(uint orderQty)
    {
        if (orderQty == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderQty), "Order quantity must be greater than 0.");
        }

        return _billOfMaterials
            .Select(line => new ComponentDemand(
                line.Component.ComponentId,
                line.Component.ComponentName,
                line.QtyPerUnit * orderQty))
            // Deterministic order: concurrent multi-line reservations take their components
            // in the same sequence, which is what keeps them from deadlocking against each
            // other on the row locks the conditional decrement takes (5.3).
            .OrderBy(demand => demand.ComponentId, StringComparer.Ordinal)
            .ToList();
    }
}
