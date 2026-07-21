using ArtificeWorks.Domain.Models.Materials;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Persistence;

/// <summary>
/// Seeds the shared-platform catalog: the component materials the factory stocks, the three
/// product lines, and their flat bills of materials.
/// <para>
/// The overlap here is not flavour text. Hermannsson Artifice Works' pitch is that its three
/// automata share ~70% of their parts, and Epic 11's dashboard renders exactly that
/// commonality — so the seed makes the claim literally true: every product is 7 shared
/// platform components plus 3 trade-specific ones, i.e. 70% shared, verifiable by counting
/// rows (and asserted in the unit tests).
/// </para>
/// Code-based and idempotent rather than raw SQL in a migration, so the intent stays
/// readable and re-running it on an existing database is a no-op.
/// </summary>
public static class CatalogSeeder
{
    // ---- The shared platform: every automaton is built on these seven. ----
    private static readonly (string Id, string Name, uint OnHand, uint QtyPerUnit)[] SharedPlatform =
    [
        ("CMP-CHASSIS-STD",    "Standard Brass Chassis",   400,  1),
        ("CMP-CORE-AETHER",    "Aether Power Core",        320,  1),
        ("CMP-CTRL-STACK",     "Control Stack",            300,  1),
        ("CMP-GOV-ESCAPEMENT", "Escapement Governor",      280,  1),
        ("CMP-LOOM-COPPER",    "Copper Wiring Loom",       900,  2),
        ("CMP-PANEL-BRASS",    "Brass Casing Panel",      1600,  4),
        ("CMP-BEARING-JEWEL",  "Jewelled Bearing Set",    2400,  6),
    ];

    // ---- Trade-specific: what makes a Custodian a Custodian. ----
    private static readonly ProductSeed[] ProductLines =
    [
        new("CUSTODIAN-STD", "Custodian",
        [
            ("CMP-HAND-BRUSH",   "Brush & Buffer Hands",  240, 2),
            ("CMP-LOCO-TREAD",   "Tread Assembly",        160, 1),
            ("CMP-SENS-DUST",    "Particulate Sensor",    180, 1),
        ]),
        new("DELVER-MINE", "Delver",
        [
            ("CMP-HAND-PICK",    "Pick & Drill Hands",    140, 2),
            ("CMP-LOCO-LEG",     "Articulated Leg Set",    90, 1),
            ("CMP-SENS-SEISMIC", "Seismic Sensor",        120, 2),
        ]),
        new("COURIER-RAPID", "Courier",
        [
            ("CMP-HAND-GRIP",    "Gripping Hands",        200, 2),
            ("CMP-LOCO-WHEEL",   "Wheel Assembly",        260, 1),
            ("CMP-SENS-NAV",     "Navigation Sensor",     150, 1),
        ]),
    ];

    /// <summary>
    /// Adds anything missing and leaves anything already present alone — including on-hand
    /// quantities, so a re-run never silently restocks a factory that has been consuming
    /// inventory.
    /// </summary>
    public static async Task SeedAsync(ArtificeWorksDbContext context, CancellationToken cancellationToken = default)
    {
        var components = await SeedComponentsAsync(context, cancellationToken);

        foreach (var line in ProductLines)
        {
            var product = await context.Products
                .Include(p => p.BillOfMaterials)
                .FirstOrDefaultAsync(p => p.ItemId == line.ProductId, cancellationToken);

            if (product is null)
            {
                product = new Product(line.ProductId, line.ProductName);
                context.Products.Add(product);
            }

            var existingComponentIds = product.BillOfMaterials
                .Select(bom => bom.Component.ComponentId)
                .ToHashSet();

            foreach (var (componentId, qtyPerUnit) in line.BomLines())
            {
                if (existingComponentIds.Contains(componentId))
                {
                    continue;
                }
                product.AddBomLine(components[componentId], qtyPerUnit);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, Component>> SeedComponentsAsync(
        ArtificeWorksDbContext context, CancellationToken cancellationToken)
    {
        var existing = await context.Components.ToDictionaryAsync(c => c.ComponentId, cancellationToken);

        var wanted = SharedPlatform
            .Select(c => (c.Id, c.Name, c.OnHand))
            .Concat(ProductLines.SelectMany(p => p.Specific.Select(c => (c.Id, c.Name, c.OnHand))));

        foreach (var (id, name, onHand) in wanted)
        {
            if (existing.ContainsKey(id))
            {
                continue;
            }
            var component = new Component(id, name, onHand);
            context.Components.Add(component);
            existing[id] = component;
        }

        return existing;
    }

    private sealed record ProductSeed(
        string ProductId,
        string ProductName,
        (string Id, string Name, uint OnHand, uint QtyPerUnit)[] Specific)
    {
        /// <summary>The product's full flat BOM: the shared platform first, then its trade parts.</summary>
        public IEnumerable<(string ComponentId, uint QtyPerUnit)> BomLines() =>
            SharedPlatform.Select(c => (c.Id, c.QtyPerUnit))
                .Concat(Specific.Select(c => (c.Id, c.QtyPerUnit)));
    }

    /// <summary>
    /// Exposed so the "70% shared" claim can be asserted rather than trusted.
    /// </summary>
    public static IReadOnlyList<string> SharedPlatformComponentIds =>
        SharedPlatform.Select(c => c.Id).ToList();

    /// <summary>The seeded product ids, in catalog order.</summary>
    public static IReadOnlyList<string> SeededProductIds =>
        ProductLines.Select(p => p.ProductId).ToList();
}
