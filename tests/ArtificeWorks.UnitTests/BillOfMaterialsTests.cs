using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// The BOM expansion rule — "what does this order actually need off the shelf?" — is the one
/// piece of picking that is pure domain logic, so it gets pure unit tests with no database.
/// </summary>
public class BillOfMaterialsTests
{
    private static (Product Product, Component Chassis, Component Bearing) ProductWithBom()
    {
        var product = new Product("TEST-001", "Test Product");
        var chassis = new Component("CMP-CHASSIS", "Chassis", onHand: 10);
        var bearing = new Component("CMP-BEARING", "Bearing Set", onHand: 100);

        product.AddBomLine(chassis, qtyPerUnit: 1);
        product.AddBomLine(bearing, qtyPerUnit: 6);

        return (product, chassis, bearing);
    }

    [Fact]
    public void Demand_is_qty_per_unit_times_order_quantity()
    {
        var (product, _, _) = ProductWithBom();

        var demand = product.ComputeDemand(orderQty: 3);

        Assert.Equal(2, demand.Count);
        Assert.Equal(18u, demand.Single(d => d.ComponentId == "CMP-BEARING").Quantity);
        Assert.Equal(3u, demand.Single(d => d.ComponentId == "CMP-CHASSIS").Quantity);
    }

    [Fact]
    public void Demand_is_ordered_by_component_id()
    {
        // Deterministic ordering is what keeps concurrent multi-line reservations from
        // deadlocking on each other's row locks (5.3) — so it's part of the contract.
        var product = new Product("TEST-001", "Test Product");
        product.AddBomLine(new Component("CMP-Z", "Last", 10), 1);
        product.AddBomLine(new Component("CMP-A", "First", 10), 1);

        var demand = product.ComputeDemand(1);

        Assert.Equal(["CMP-A", "CMP-Z"], demand.Select(d => d.ComponentId));
    }

    /// <summary>
    /// 13.2's load-bearing non-change. A manufactured component is one row in the demand like any
    /// other — picking draws it off the shelf and does not care that the factory made it. The
    /// moment this test needs updating, picking has quietly turned into a recursive planner.
    /// </summary>
    [Fact]
    public void A_made_component_is_demanded_exactly_like_a_bought_one()
    {
        var product = new Product("TEST-001", "Test Product");
        product.AddBomLine(new Component("CMP-BOUGHT", "Bought Part", onHand: 10), 1);
        product.AddBomLine(
            new Component("CMP-MADE", "Made Sub-assembly", onHand: 10, makeProductId: "SUBASM-001"), 2);

        var demand = product.ComputeDemand(orderQty: 3);

        Assert.Equal(2, demand.Count);
        Assert.Equal(6u, demand.Single(d => d.ComponentId == "CMP-MADE").Quantity);
        // Nothing in the demand row says where the part came from — that is the point.
        Assert.Equal(3u, demand.Single(d => d.ComponentId == "CMP-BOUGHT").Quantity);
    }

    [Fact]
    public void A_product_without_a_bom_demands_nothing()
    {
        Assert.Empty(new Product("TEST-002", "Bare Product").ComputeDemand(5));
    }

    [Fact]
    public void The_same_component_cannot_appear_twice_in_one_bom()
    {
        var product = new Product("TEST-001", "Test Product");
        var chassis = new Component("CMP-CHASSIS", "Chassis", 10);
        product.AddBomLine(chassis, 1);

        Assert.Throws<InvalidOperationException>(() => product.AddBomLine(chassis, 2));
    }

    [Fact]
    public void A_bom_line_must_call_for_at_least_one_component()
    {
        var product = new Product("TEST-001", "Test Product");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => product.AddBomLine(new Component("CMP-CHASSIS", "Chassis", 10), qtyPerUnit: 0));
    }

    [Fact]
    public void Consuming_more_than_is_on_hand_takes_nothing()
    {
        var component = new Component("CMP-CHASSIS", "Chassis", onHand: 2);

        Assert.False(component.TryConsume(3));
        Assert.Equal(2u, component.OnHand);

        Assert.True(component.TryConsume(2));
        Assert.Equal(0u, component.OnHand);
    }

    [Fact]
    public void A_reservation_records_every_demanded_line()
    {
        var (product, _, _) = ProductWithBom();
        var demand = product.ComputeDemand(2);

        var reservation = new MaterialReservation(Guid.NewGuid(), attemptNumber: 1, demand);

        Assert.Equal(2, reservation.Lines.Count);
        Assert.Equal(12u, reservation.Lines.Single(l => l.ComponentId == "CMP-BEARING").Quantity);
        Assert.Contains("12× CMP-BEARING", reservation.Describe());
    }

    /// <summary>
    /// 13.1. A rebuild's pick can draw exactly the same lines as an earlier one, so the attempt has
    /// to be in the description or the timeline shows one sentence twice and means two draws.
    /// </summary>
    [Fact]
    public void A_rebuilds_reservation_names_its_attempt_so_two_picks_never_read_alike()
    {
        var (product, _, _) = ProductWithBom();
        var demand = product.ComputeDemand(2);
        var workOrderId = Guid.NewGuid();

        var initial = new MaterialReservation(workOrderId, attemptNumber: 1, demand);
        var rebuild = new MaterialReservation(workOrderId, attemptNumber: 2, demand);

        // The initial pick keeps its original wording — there is nothing to disambiguate it from.
        Assert.DoesNotContain("attempt", initial.Describe());
        Assert.Contains("rebuild attempt 2", rebuild.Describe());
        Assert.NotEqual(initial.Describe(), rebuild.Describe());
    }

    [Fact]
    public void A_reservation_cannot_be_recorded_against_a_nonsense_attempt()
    {
        var (product, _, _) = ProductWithBom();
        var demand = product.ComputeDemand(1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MaterialReservation(Guid.NewGuid(), attemptNumber: 0, demand));
    }
}
