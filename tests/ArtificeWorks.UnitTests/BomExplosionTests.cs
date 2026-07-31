using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// The multi-level BOM walk (13.2). Pure domain, no database — which is the point of putting the
/// recursion in <see cref="BomExplosion"/> rather than in a query.
/// <para>
/// The refusals come first on purpose. Everything else here is a wrong number on a screen; a walk
/// that does not refuse a cycle is, once 13.3 spawns work orders from it, a work-order generator
/// pointed at a shared public demo.
/// </para>
/// </summary>
public class BomExplosionTests
{
    private static IReadOnlyDictionary<string, Product> Catalog(params Product[] products) =>
        products.ToDictionary(p => p.ItemId, StringComparer.Ordinal);

    // ------------------------------------------------------------------ the refusals

    [Fact]
    public void A_cycle_is_refused_and_the_error_names_the_component()
    {
        // WIDGET is built from a GEAR, and the gear's own assembly calls for a widget. Neither edit
        // is illegal on its own — which is exactly why no aggregate can catch this and the walk has
        // to.
        var widgetLine = new Product("PROD-WIDGET", "Widget");
        var gearAssembly = new Product("SUBASM-GEAR", "Gear Assembly");

        var gear = new Component("CMP-GEAR", "Gear", onHand: 10, makeProductId: "SUBASM-GEAR");
        var widget = new Component("CMP-WIDGET", "Widget Body", onHand: 10, makeProductId: "PROD-WIDGET");

        widgetLine.AddBomLine(gear, 1);
        gearAssembly.AddBomLine(widget, 1);

        var error = Assert.Throws<BomExplosionException>(
            () => BomExplosion.Explode(widgetLine, 1, Catalog(widgetLine, gearAssembly)));

        Assert.Contains("CMP-WIDGET", error.Message);
        Assert.Contains("PROD-WIDGET", error.Message);
        Assert.Contains("cyclic", error.Message);
    }

    [Fact]
    public void A_product_that_makes_a_component_cannot_also_consume_it()
    {
        // The one-step cycle, caught in the aggregate before it can ever reach the walk.
        var product = new Product("PROD-WIDGET", "Widget");
        var itsOwnOutput = new Component("CMP-WIDGET", "Widget Body", 10, makeProductId: "PROD-WIDGET");

        var error = Assert.Throws<InvalidOperationException>(() => product.AddBomLine(itsOwnOutput, 1));
        Assert.Contains("CMP-WIDGET", error.Message);
    }

    [Fact]
    public void A_component_cannot_be_its_own_maker()
    {
        Assert.Throws<InvalidOperationException>(
            () => new Component("CMP-GEAR", "Gear", 10, makeProductId: "CMP-GEAR"));
    }

    [Fact]
    public void A_tree_deeper_than_the_cap_is_refused()
    {
        // A chain of assemblies, each making the component the one above it consumes: level 0's
        // product consumes level 1's output, and so on. Not a cycle — just deeper than anything
        // sane, which is the case the cap exists for.
        var chainLength = BomExplosion.MaxDepth + 1;
        var products = Enumerable.Range(0, chainLength + 1)
            .Select(i => new Product($"PROD-{i}", $"Level {i}"))
            .ToArray();

        for (var i = 0; i < chainLength; i++)
        {
            products[i].AddBomLine(
                new Component($"CMP-{i}", $"Part {i}", onHand: 5, makeProductId: $"PROD-{i + 1}"), 1);
        }
        // A bought part at the bottom, so the walk would otherwise terminate happily.
        products[chainLength].AddBomLine(new Component("CMP-LEAF", "Leaf", 5), 1);

        var error = Assert.Throws<BomExplosionException>(
            () => BomExplosion.Explode(products[0], 1, Catalog(products)));

        Assert.Contains($"{BomExplosion.MaxDepth}-level limit", error.Message);
    }

    [Fact]
    public void A_made_component_whose_maker_is_missing_is_an_error_not_a_leaf()
    {
        // Treating it as bought would silently understate demand — a wrong answer is worse than a
        // refusal, because nothing downstream would know to doubt it.
        var product = new Product("PROD-WIDGET", "Widget");
        product.AddBomLine(new Component("CMP-GEAR", "Gear", 10, makeProductId: "SUBASM-GONE"), 1);

        var error = Assert.Throws<BomExplosionException>(
            () => BomExplosion.Explode(product, 1, Catalog(product)));

        Assert.Contains("CMP-GEAR", error.Message);
        Assert.Contains("SUBASM-GONE", error.Message);
        Assert.Contains("not in the catalog", error.Message);
    }

    // ------------------------------------------------------------------ the walk

    /// <summary>
    /// CART is 2 wheels + 1 axle. A wheel is made (2 spokes + 1 hub); an axle is bought.
    /// </summary>
    private static (Product Cart, Product WheelAssembly) TwoLevelCatalog()
    {
        var cart = new Product("PROD-CART", "Cart");
        var wheelAssembly = new Product("SUBASM-WHEEL", "Wheel Assembly");

        cart.AddBomLine(new Component("CMP-WHEEL", "Wheel", onHand: 12, makeProductId: "SUBASM-WHEEL"), 2);
        cart.AddBomLine(new Component("CMP-AXLE", "Axle", onHand: 40), 1);

        wheelAssembly.AddBomLine(new Component("CMP-SPOKE", "Spoke", onHand: 500), 2);
        wheelAssembly.AddBomLine(new Component("CMP-HUB", "Hub", onHand: 60), 1);

        return (cart, wheelAssembly);
    }

    [Fact]
    public void Extended_quantities_multiply_all_the_way_down_the_tree()
    {
        var (cart, wheelAssembly) = TwoLevelCatalog();

        var explosion = BomExplosion.Explode(cart, orderQty: 3, Catalog(cart, wheelAssembly));

        var wheel = explosion.Tree.Single(n => n.ComponentId == "CMP-WHEEL");
        Assert.True(wheel.IsMade);
        Assert.Equal("SUBASM-WHEEL", wheel.MakeProductId);
        Assert.Equal(2u, wheel.QtyPerParent);   // per cart
        Assert.Equal(6u, wheel.ExtendedQty);    // 2 × 3 carts
        Assert.Equal(1, wheel.Depth);
        Assert.Equal(12u, wheel.OnHand);

        var spoke = wheel.Children.Single(n => n.ComponentId == "CMP-SPOKE");
        Assert.False(spoke.IsMade);
        Assert.Equal(2u, spoke.QtyPerParent);   // per wheel
        Assert.Equal(12u, spoke.ExtendedQty);   // 2 × 6 wheels
        Assert.Equal(2, spoke.Depth);
        Assert.Empty(spoke.Children);
    }

    [Fact]
    public void Leaf_demand_is_the_bought_parts_only_and_is_ordered_like_ComputeDemand()
    {
        var (cart, wheelAssembly) = TwoLevelCatalog();

        var explosion = BomExplosion.Explode(cart, orderQty: 3, Catalog(cart, wheelAssembly));

        // CMP-WHEEL is made, so it is a node in the tree but never a row in leaf demand.
        Assert.Equal(["CMP-AXLE", "CMP-HUB", "CMP-SPOKE"], explosion.LeafDemand.Select(d => d.ComponentId));
        Assert.Equal(3u, explosion.LeafDemand.Single(d => d.ComponentId == "CMP-AXLE").Quantity);
        Assert.Equal(6u, explosion.LeafDemand.Single(d => d.ComponentId == "CMP-HUB").Quantity);
        Assert.Equal(12u, explosion.LeafDemand.Single(d => d.ComponentId == "CMP-SPOKE").Quantity);
    }

    [Fact]
    public void A_component_reached_by_two_paths_aggregates_into_one_leaf_row()
    {
        // A diamond, not a cycle: the same bolt is called for by the frame directly and by the
        // wheel assembly underneath it. Both paths are walked; the buyer sees one number.
        var cart = new Product("PROD-CART", "Cart");
        var wheelAssembly = new Product("SUBASM-WHEEL", "Wheel Assembly");

        cart.AddBomLine(new Component("CMP-WHEEL", "Wheel", 12, makeProductId: "SUBASM-WHEEL"), 2);
        cart.AddBomLine(new Component("CMP-BOLT", "Bolt", 900), 5);
        wheelAssembly.AddBomLine(new Component("CMP-BOLT", "Bolt", 900), 4);

        var explosion = BomExplosion.Explode(cart, orderQty: 1, Catalog(cart, wheelAssembly));

        var bolt = Assert.Single(explosion.LeafDemand, d => d.ComponentId == "CMP-BOLT");
        Assert.Equal(13u, bolt.Quantity); // 5 on the frame + (4 per wheel × 2 wheels)

        // ...and the tree still shows both places it is used, because that is what a tree is for.
        Assert.Equal(5u, explosion.Tree.Single(n => n.ComponentId == "CMP-BOLT").ExtendedQty);
        Assert.Equal(8u, explosion.Tree.Single(n => n.ComponentId == "CMP-WHEEL")
            .Children.Single(n => n.ComponentId == "CMP-BOLT").ExtendedQty);
    }

    [Fact]
    public void The_same_component_is_expanded_on_every_path_it_appears_on()
    {
        // The regression that would follow from using a visited *set* instead of a path: the second
        // branch would come back childless.
        var machine = new Product("PROD-MACHINE", "Machine");
        var gearbox = new Product("SUBASM-GEARBOX", "Gearbox");

        var gearboxComponent = new Component("CMP-GEARBOX", "Gearbox", 10, makeProductId: "SUBASM-GEARBOX");
        gearbox.AddBomLine(new Component("CMP-COG", "Cog", 200), 3);

        // Two products, both calling for the same made component.
        machine.AddBomLine(gearboxComponent, 2);
        var trolley = new Product("PROD-TROLLEY", "Trolley");
        trolley.AddBomLine(gearboxComponent, 1);

        var catalog = Catalog(machine, trolley, gearbox);

        Assert.Equal(6u, BomExplosion.Explode(machine, 1, catalog)
            .LeafDemand.Single(d => d.ComponentId == "CMP-COG").Quantity);
        Assert.Equal(3u, BomExplosion.Explode(trolley, 1, catalog)
            .LeafDemand.Single(d => d.ComponentId == "CMP-COG").Quantity);
    }

    /// <summary>
    /// The regression that proves 13.2 changed nothing for a product with no made components —
    /// which, at the flat level, is what the whole "no behaviour changes" claim rests on.
    /// </summary>
    [Fact]
    public void A_product_with_no_made_components_explodes_to_exactly_its_flat_bom()
    {
        var product = new Product("PROD-FLAT", "Flat Product");
        product.AddBomLine(new Component("CMP-B", "Bee", 10), 2);
        product.AddBomLine(new Component("CMP-A", "Ay", 10), 3);

        var explosion = BomExplosion.Explode(product, orderQty: 4, Catalog(product));

        Assert.All(explosion.Tree, node => Assert.Empty(node.Children));
        Assert.All(explosion.Tree, node => Assert.False(node.IsMade));

        // Line for line, the same answer picking would compute.
        var flat = product.ComputeDemand(4);
        Assert.Equal(
            flat.Select(d => (d.ComponentId, d.ComponentName, d.Quantity)),
            explosion.LeafDemand.Select(d => (d.ComponentId, d.ComponentName, d.Quantity)));
    }

    [Fact]
    public void A_product_without_a_bom_explodes_to_nothing()
    {
        var bare = new Product("PROD-BARE", "Bare Product");

        var explosion = BomExplosion.Explode(bare, 5, Catalog(bare));

        Assert.Empty(explosion.Tree);
        Assert.Empty(explosion.LeafDemand);
        Assert.Equal(5u, explosion.OrderQty);
    }

    [Fact]
    public void Exploding_for_no_units_is_refused()
    {
        var (cart, wheelAssembly) = TwoLevelCatalog();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BomExplosion.Explode(cart, orderQty: 0, Catalog(cart, wheelAssembly)));
    }
}
