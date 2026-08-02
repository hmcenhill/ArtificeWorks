using ArtificeWorks.Domain.Models.Materials;

using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Infrastructure.Persistence;

/// <summary>
/// Seeds the shared-platform catalog: the component materials the factory stocks, the three
/// product lines, the sub-assemblies two of their shared parts are built from, and every bill of
/// materials.
/// <para>
/// The overlap here is not flavour text. Hermannsson Artifice Works' pitch is that its three
/// automata share ~70% of their parts, and Epic 11's dashboard renders exactly that commonality —
/// so the seed makes the claim literally true, at two levels, and the unit and integration tests
/// assert both numbers rather than trusting them:
/// </para>
/// <list type="bullet">
///   <item><description><strong>70% of the flat BOM</strong> — every product is 7 shared platform
///   components plus 3 trade-specific ones.</description></item>
///   <item><description><strong>80% of the exploded leaves</strong> — 12 of the 15 bought
///   materials underneath a finished automaton are common to all three lines.</description></item>
///   <item><description><strong>Two shared sub-assemblies</strong> (three, counting the core
///   casing nested inside one of them) — the control stack and the power core are <em>built</em>,
///   once, and consumed by all three lines. That is what a shared platform actually means, and it
///   is what the flat seven rows could only gesture at.</description></item>
/// </list>
/// Code-based and idempotent rather than raw SQL in a migration, so the intent stays
/// readable and re-running it on an existing database is a no-op.
/// </summary>
public static class CatalogSeeder
{
    // ---- The shared platform: every automaton is built on these seven. ----
    // Two of them — the control stack and the power core — the factory makes rather than buys;
    // that is declared by the sub-assemblies below, not here, so this stays the list of "what goes
    // into an automaton" regardless of where each part comes from.
    //
    // 13.3 deliberately stocks those two THIN (24 each, against 280–2400 for the bought parts).
    // The child-order loop is the epic's showpiece and it only fires when a made component runs out,
    // so a shelf sized like the bought ones would mean nobody ever sees it. 24 is roughly eight
    // simulated orders — often enough that a visitor watching for a few minutes sees the board
    // deepen, rare enough that most orders still run straight through.
    //
    // These three numbers are the demo's pacing dial. Raise them if children are appearing so often
    // that the board is mostly sub-assemblies; lower them if the showpiece never shows. 10.4's sweep
    // restocks to whatever is set here, so the world keeps healing either way.
    private static readonly (string Id, string Name, uint OnHand, uint QtyPerUnit)[] SharedPlatform =
    [
        ("CMP-CHASSIS-STD",    "Standard Brass Chassis",   400,  1),
        ("CMP-CORE-AETHER",    "Aether Power Core",         24,  1),
        ("CMP-CTRL-STACK",     "Control Stack",             24,  1),
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

    // ---- What the factory makes rather than buys: the depth in the multi-level BOM (13.2). ----
    // Each entry is a product whose whole reason to exist is to produce one component. The nesting
    // is deliberate and not decoration: SUBASM-CORE calls for CMP-CASING-CORE, which is itself made
    // by SUBASM-CORE-CASING — so the recursion is exercised by real seed data on every run, not
    // only by a test fixture that could drift from it.
    //
    // A part may be declared in more than one seed's Parts list (component seeding is
    // first-declaration-wins); CMP-CASING-CORE is declared where it is consumed and made where it
    // is built, which keeps each list readable as "what this assembly is made of".
    private static readonly SubAssemblySeed[] SubAssemblies =
    [
        new("SUBASM-CTRL-STACK", "Control Stack Assembly", Makes: "CMP-CTRL-STACK",
        [
            ("CMP-LINK-GOVERNOR", "Governor Linkage",  300, 1),
            ("CMP-DRUM-LOGIC",    "Logic Drum",        300, 1),
            ("CMP-BANK-RELAY",    "Relay Bank",        600, 2),
        ]),
        new("SUBASM-CORE", "Aether Core Assembly", Makes: "CMP-CORE-AETHER",
        [
            ("CMP-CELL-AETHER",   "Aether Cell",       320, 1),
            ("CMP-REG-FLUX",      "Flux Regulator",    320, 1),
            // Thinner still, and on purpose: this is the *nested* made component, so draining it is
            // what produces a grandchild — a core assembly that has to schedule its own casing.
            // Two levels of spawned work is what proves the recursion is real rather than asserted.
            ("CMP-CASING-CORE",   "Core Casing",        12, 1),
        ]),
        new("SUBASM-CORE-CASING", "Core Casing Assembly", Makes: "CMP-CASING-CORE",
        [
            ("CMP-SHELL-LEAD",    "Leaded Shell",      340, 1),
            ("CMP-GASKET-SEAL",   "Sealing Gasket",    680, 2),
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

        foreach (var seed in ProductLines.Cast<IProductSeed>().Concat(SubAssemblies))
        {
            var product = await context.Products
                .Include(p => p.BillOfMaterials)
                .FirstOrDefaultAsync(p => p.ItemId == seed.ProductId, cancellationToken);

            if (product is null)
            {
                product = new Product(seed.ProductId, seed.ProductName);
                context.Products.Add(product);
            }

            var existingComponentIds = product.BillOfMaterials
                .Select(bom => bom.Component.ComponentId)
                .ToHashSet();

            foreach (var (componentId, qtyPerUnit) in seed.BomLines())
            {
                if (existingComponentIds.Contains(componentId))
                {
                    continue;
                }
                product.AddBomLine(components[componentId], qtyPerUnit);
            }
        }

        // The make links are applied after the products exist, because a component points at the
        // product that builds it. Unlike on-hand, this is *structure* rather than state — there is
        // no live value to stomp — so setting it on a re-run is a correct backfill for a database
        // seeded before 13.2 rather than a re-seed of something in flight.
        foreach (var subAssembly in SubAssemblies)
        {
            var made = components[subAssembly.Makes];
            if (made.MakeProductId != subAssembly.ProductId)
            {
                made.MarkMadeBy(subAssembly.ProductId);
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
            .Concat(ProductLines.SelectMany(p => p.Specific.Select(c => (c.Id, c.Name, c.OnHand))))
            .Concat(SubAssemblies.SelectMany(s => s.Parts.Select(c => (c.Id, c.Name, c.OnHand))));

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

    /// <summary>A product the seeder creates, and the BOM lines it should end up with.</summary>
    private interface IProductSeed
    {
        string ProductId { get; }
        string ProductName { get; }
        IEnumerable<(string ComponentId, uint QtyPerUnit)> BomLines();
    }

    private sealed record ProductSeed(
        string ProductId,
        string ProductName,
        (string Id, string Name, uint OnHand, uint QtyPerUnit)[] Specific) : IProductSeed
    {
        /// <summary>The product's full flat BOM: the shared platform first, then its trade parts.</summary>
        public IEnumerable<(string ComponentId, uint QtyPerUnit)> BomLines() =>
            SharedPlatform.Select(c => (c.Id, c.QtyPerUnit))
                .Concat(Specific.Select(c => (c.Id, c.QtyPerUnit)));
    }

    /// <summary>
    /// A product that exists to build one component. <paramref name="Makes"/> is the component id
    /// its output is credited to — the link that turns the flat BOM into a tree.
    /// </summary>
    private sealed record SubAssemblySeed(
        string ProductId,
        string ProductName,
        string Makes,
        (string Id, string Name, uint OnHand, uint QtyPerUnit)[] Parts) : IProductSeed
    {
        public IEnumerable<(string ComponentId, uint QtyPerUnit)> BomLines() =>
            Parts.Select(c => (c.Id, c.QtyPerUnit));
    }

    /// <summary>
    /// Exposed so the "70% shared" claim can be asserted rather than trusted.
    /// </summary>
    public static IReadOnlyList<string> SharedPlatformComponentIds =>
        SharedPlatform.Select(c => c.Id).ToList();

    /// <summary>
    /// The seeded <em>saleable</em> product ids, in catalog order — the three automata a customer
    /// orders. <strong>Deliberately excludes the sub-assemblies</strong>: 10.3's order generator
    /// picks from this list, and a simulated customer ordering a bare core casing would be
    /// nonsense on the board. See <see cref="SubAssemblyProductIds"/> for those.
    /// </summary>
    public static IReadOnlyList<string> SeededProductIds =>
        ProductLines.Select(p => p.ProductId).ToList();

    /// <summary>
    /// The products that exist to build a component rather than to be sold, in catalog order.
    /// </summary>
    public static IReadOnlyList<string> SubAssemblyProductIds =>
        SubAssemblies.Select(s => s.ProductId).ToList();

    /// <summary>The components the factory manufactures, mapped to the product that builds each.</summary>
    public static IReadOnlyDictionary<string, string> MadeComponents =>
        SubAssemblies.ToDictionary(s => s.Makes, s => s.ProductId);
}
