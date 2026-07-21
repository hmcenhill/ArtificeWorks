namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// One line of a <see cref="Product"/>'s flat bill of materials: how many of a given
/// <see cref="Component"/> one finished unit needs. Flat by design for this epic — a BOM
/// line points at a raw component, never at another product. Epic 13 introduces multi-level
/// BOMs, at which point this gains a nested-assembly notion.
/// </summary>
public class BomLine
{
    public Guid Id { get; }
    public string ProductId { get; }
    public Component Component { get; }

    /// <summary>Components consumed per single finished unit of the product.</summary>
    public uint QtyPerUnit { get; }

    private BomLine() { }

    public BomLine(Product product, Component component, uint qtyPerUnit)
    {
        if (qtyPerUnit == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qtyPerUnit), "A BOM line must call for at least one component.");
        }

        Id = Guid.NewGuid();
        ProductId = product.ItemId;
        Component = component;
        QtyPerUnit = qtyPerUnit;
    }
}
