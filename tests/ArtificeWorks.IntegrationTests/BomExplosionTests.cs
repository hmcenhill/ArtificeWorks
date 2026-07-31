using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 13.2 against a real database and a real API: the seeded catalog is genuinely a tree, it
/// explodes cleanly, and the shared-platform claim holds at the level the deepened catalog makes
/// true.
/// <para>
/// The unit suite proves the walk. What can only be proved here is that the <em>data</em> matches
/// it — that the make links survive a round trip through Postgres, that the seeder is still
/// idempotent now it writes structure as well as rows, and that the pitch and the catalog have not
/// drifted apart.
/// </para>
/// </summary>
public class BomExplosionTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public BomExplosionTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    // The wire shapes, read into test-local records because the production DTOs are serialize-only.
    private sealed record BomNodeResponse(
        string ComponentId, string ComponentName, uint QtyPerParent, uint ExtendedQty,
        int Depth, bool IsMade, string? MakeProductId, uint OnHand, BomNodeResponse[] Children);

    private sealed record LeafDemandResponse(string ComponentId, string ComponentName, uint Quantity);

    private sealed record ProductBomResponse(
        string ItemId, string ItemName, uint OrderQty,
        BomNodeResponse[] Tree, LeafDemandResponse[] LeafDemand);

    private sealed record ProductSummaryResponse(string ItemId, string ItemName, bool IsSubAssembly);

    // ------------------------------------------------------------------ the seeded catalog

    /// <summary>
    /// The shared-platform pitch, restated at the level 13.2 makes true and asserted rather than
    /// adjusted to whatever the seed happens to produce. If these numbers move, the seeder's doc
    /// comment moves with them — the pitch and the data must not drift.
    /// </summary>
    [Fact]
    public async Task The_three_lines_share_their_sub_assemblies_and_eighty_percent_of_their_leaves()
    {
        var catalog = await SeededCatalog();
        var lines = CatalogSeeder.SeededProductIds.Select(id => catalog[id]).ToList();
        Assert.Equal(3, lines.Count);

        var explosions = lines.ToDictionary(p => p.ItemId, p => BomExplosion.Explode(p, 1, catalog));

        // ---- shared sub-assemblies: the thing seven flat rows could only gesture at ----
        var madeInEveryLine = explosions.Values
            .Select(e => e.Tree.Where(n => n.IsMade).Select(n => n.ComponentId).ToHashSet())
            .Aggregate((common, next) => { common.IntersectWith(next); return common; });

        Assert.Equal(["CMP-CORE-AETHER", "CMP-CTRL-STACK"], madeInEveryLine.Order());

        // ...and one of them is itself built from a made component, so the seed really is two
        // levels deep and the recursion is exercised by real data on every run.
        var core = explosions.Values.First().Tree.Single(n => n.ComponentId == "CMP-CORE-AETHER");
        var casing = core.Children.Single(n => n.ComponentId == "CMP-CASING-CORE");
        Assert.True(casing.IsMade);
        Assert.NotEmpty(casing.Children);

        // ---- shared leaves: 12 of every line's 15 bought materials ----
        var leaves = explosions.ToDictionary(
            e => e.Key,
            e => e.Value.LeafDemand.Select(d => d.ComponentId).ToHashSet());

        var sharedLeaves = leaves.Values
            .Aggregate(new HashSet<string>(leaves.Values.First()),
                (common, next) => { common.IntersectWith(next); return common; });

        Assert.Equal(12, sharedLeaves.Count);
        foreach (var bought in leaves.Values)
        {
            Assert.Equal(15, bought.Count);
            Assert.Equal(0.8, (double)sharedLeaves.Count / bought.Count, precision: 2);
            Assert.NotEmpty(bought.Except(sharedLeaves)); // each line keeps its own trade parts
        }

        // The flat claim from Epic 5 is still true too — 70% of ten rows — and is asserted where it
        // always was, in MaterialPickingTests. Both numbers are real; they measure different things.
    }

    [Fact]
    public async Task Every_seeded_product_explodes_and_every_component_carries_stock()
    {
        var catalog = await SeededCatalog();

        // Sub-assemblies included: 13.3 will build them, and a sub-assembly whose own BOM cannot be
        // resolved is a bug that would only surface then.
        var seeded = CatalogSeeder.SeededProductIds.Concat(CatalogSeeder.SubAssemblyProductIds);

        foreach (var productId in seeded)
        {
            Assert.NotEmpty(BomExplosion.Explode(catalog[productId], orderQty: 1, catalog).Tree);
        }

        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        // Every component the catalog reaches starts on the shelf — made ones included. Without
        // that, 13.2 would break every order the moment it merged and 13.3's spawn would be
        // load-bearing before it is written.
        var reachable = catalog.Values
            .SelectMany(p => p.BillOfMaterials.Select(l => l.Component.ComponentId))
            .Distinct()
            .ToList();

        Assert.False(
            await context.Components.AnyAsync(c => reachable.Contains(c.ComponentId) && c.OnHand == 0),
            "every seeded component must carry stock — the pipeline still has to run");

        foreach (var (componentId, makerId) in CatalogSeeder.MadeComponents)
        {
            var made = await context.Components.AsNoTracking()
                .SingleAsync(c => c.ComponentId == componentId);
            Assert.Equal(makerId, made.MakeProductId);
            Assert.True(made.IsMade);
        }
    }

    [Fact]
    public async Task Re_running_the_seeder_neither_duplicates_the_tree_nor_re_links_it()
    {
        await Seed();
        await Seed();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        // One sub-assembly product per made component, and no duplicated BOM lines under them.
        foreach (var subAssemblyId in CatalogSeeder.SubAssemblyProductIds)
        {
            Assert.Equal(1, await context.Products.CountAsync(p => p.ItemId == subAssemblyId));
        }

        Assert.Equal(3, await context.BomLines.CountAsync(l => l.ProductId == "SUBASM-CTRL-STACK"));
        Assert.Equal(
            CatalogSeeder.MadeComponents.Count,
            await context.Components.CountAsync(c => c.MakeProductId != null));
    }

    // ------------------------------------------------------------------------ the API

    [Fact]
    public async Task The_bom_endpoint_returns_a_tree_the_client_can_draw_without_a_second_call()
    {
        await Seed();

        var response = await _fixture.Client.GetAsync("/products/CUSTODIAN-STD/bom?qty=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bom = await response.Content.ReadFromJsonAsync<ProductBomResponse>();
        Assert.NotNull(bom);

        Assert.Equal("CUSTODIAN-STD", bom.ItemId);
        Assert.Equal(2u, bom.OrderQty);
        Assert.Equal(10, bom.Tree.Length); // the flat BOM is still the top level

        var core = bom.Tree.Single(n => n.ComponentId == "CMP-CORE-AETHER");
        Assert.True(core.IsMade);
        Assert.Equal("SUBASM-CORE", core.MakeProductId);
        Assert.Equal(1u, core.QtyPerParent);
        Assert.Equal(2u, core.ExtendedQty); // one per automaton, two automata
        Assert.Equal(1, core.Depth);

        // Two levels below the finished product, reachable without another request.
        var casing = core.Children.Single(n => n.ComponentId == "CMP-CASING-CORE");
        Assert.True(casing.IsMade);
        Assert.Equal(2, casing.Depth);
        var gasket = casing.Children.Single(n => n.ComponentId == "CMP-GASKET-SEAL");
        Assert.False(gasket.IsMade);
        Assert.Equal(3, gasket.Depth);
        Assert.Equal(2u, gasket.QtyPerParent);
        Assert.Equal(4u, gasket.ExtendedQty); // 2 per casing × 1 casing per core × 2 automata

        // A bought part every line shares: 2 looms per automaton × 2.
        Assert.Equal(4u, bom.LeafDemand.Single(d => d.ComponentId == "CMP-LOOM-COPPER").Quantity);

        // Leaf demand is bought parts only — a made component is a node, never a row.
        Assert.DoesNotContain(bom.LeafDemand, d => d.ComponentId == "CMP-CORE-AETHER");
        Assert.Equal(15, bom.LeafDemand.Length);
    }

    [Fact]
    public async Task The_bom_endpoint_defaults_to_one_unit()
    {
        await Seed();

        var bom = await _fixture.Client.GetFromJsonAsync<ProductBomResponse>("/products/CUSTODIAN-STD/bom");

        Assert.NotNull(bom);
        Assert.Equal(1u, bom.OrderQty);

        // Per unit, extended and per-parent quantities agree at the top level.
        Assert.All(bom.Tree, node => Assert.Equal(node.QtyPerParent, node.ExtendedQty));
    }

    [Fact]
    public async Task An_unknown_product_has_no_bom()
    {
        var response = await _fixture.Client.GetAsync("/products/Does-Not-Exist/bom");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("product_not_found", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Exploding_for_no_units_is_a_validation_failure()
    {
        await Seed();

        var response = await _fixture.Client.GetAsync("/products/CUSTODIAN-STD/bom?qty=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task The_catalog_listing_tells_an_automaton_from_a_sub_assembly()
    {
        await Seed();

        var products = await _fixture.Client
            .GetFromJsonAsync<List<ProductSummaryResponse>>("/products");
        Assert.NotNull(products);

        // Sub-assemblies are listed — they are ordinary products — but flagged, so the create form
        // can offer only the things a customer orders.
        foreach (var subAssemblyId in CatalogSeeder.SubAssemblyProductIds)
        {
            Assert.True(products.Single(p => p.ItemId == subAssemblyId).IsSubAssembly);
        }
        foreach (var lineId in CatalogSeeder.SeededProductIds)
        {
            Assert.False(products.Single(p => p.ItemId == lineId).IsSubAssembly);
        }
    }

    // ------------------------------------------------------------------------- helpers

    /// <summary>
    /// The fixture migrates after the host is built, so <c>CatalogSeeder</c> has already declined to
    /// run at startup and every test class seeds what it needs. The seeder is idempotent, so calling
    /// this from each test is a no-op after the first.
    /// </summary>
    private async Task Seed()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
        await CatalogSeeder.SeedAsync(context);
    }

    private async Task<Dictionary<string, Product>> SeededCatalog()
    {
        await Seed();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        var products = await context.Products
            .AsNoTracking()
            .Include(p => p.BillOfMaterials)
                .ThenInclude(line => line.Component)
            .ToListAsync();

        return products.ToDictionary(p => p.ItemId, StringComparer.Ordinal);
    }
}
