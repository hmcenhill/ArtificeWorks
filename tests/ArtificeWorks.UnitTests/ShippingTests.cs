using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Shipping;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// The parcel's own rules (7.1) and the carrier seam (7.1/7.3), away from a database. The
/// interesting claim here is that dispatch's idempotency is a <em>state machine</em>, not a
/// unique index — Epic 7 is the first stage whose "happens once" guarantee doesn't need the
/// database to enforce it.
/// </summary>
public class ShippingTests
{
    private static Shipment ABookedShipment(params Guid[] serials) =>
        new(Guid.NewGuid(), "Ravenscroft Haulage", "RH-ABC1234567",
            DateTime.UtcNow.AddDays(3), serials.Length > 0 ? serials : [Guid.NewGuid()]);

    // ------------------------------------------------------------------------ the shipment

    [Fact]
    public void A_booked_shipment_names_a_carrier_a_tracking_number_and_its_units()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var shipment = ABookedShipment(first, second);

        Assert.Equal(ShipmentStatus.Booked, shipment.Status);
        Assert.Null(shipment.DispatchedUtc);
        Assert.Equal("Ravenscroft Haulage", shipment.Carrier);
        Assert.Equal([first, second], shipment.Lines.Select(line => line.SerialNumber));
        Assert.True(shipment.EstimatedArrivalUtc > shipment.BookedUtc);
    }

    [Fact]
    public void A_shipment_cannot_be_booked_empty_or_anonymous()
    {
        var workOrderId = Guid.NewGuid();
        var eta = DateTime.UtcNow.AddDays(3);

        // An empty parcel is the failure mode that matters: it would mean an order reached
        // Delivery with nothing that passed, and shipping air should not be representable.
        Assert.Throws<ArgumentException>(() => new Shipment(workOrderId, "Carrier", "TRK-1", eta, []));
        Assert.Throws<ArgumentException>(() => new Shipment(workOrderId, "", "TRK-1", eta, [Guid.NewGuid()]));
        Assert.Throws<ArgumentException>(() => new Shipment(workOrderId, "Carrier", " ", eta, [Guid.NewGuid()]));
    }

    [Fact]
    public void Dispatch_happens_exactly_once_and_that_is_the_whole_idempotency_story()
    {
        var shipment = ABookedShipment();

        var first = shipment.Dispatch();

        Assert.True(first.Success);
        Assert.Equal(ShipmentStatus.Dispatched, shipment.Status);
        Assert.NotNull(shipment.DispatchedUtc);

        // A redelivered ShipmentScheduled lands here. No run table, no unique index: the
        // shipment's own transition refuses the second hand-over.
        var replay = shipment.Dispatch();

        Assert.False(replay.Success);
        Assert.Equal(TransitionErrorCode.InvalidTransition, replay.Code);
    }

    [Fact]
    public void A_cancelled_order_voids_a_booked_parcel_but_never_a_dispatched_one()
    {
        var booked = ABookedShipment();
        Assert.True(booked.Void().Success);
        Assert.Equal(ShipmentStatus.Cancelled, booked.Status);
        Assert.False(booked.Dispatch().Success);

        var dispatched = ABookedShipment();
        dispatched.Dispatch();

        // After dispatch the question can't arise through the API — Completed refuses Cancel —
        // but the domain says no on its own rather than relying on that.
        var voided = dispatched.Void();
        Assert.False(voided.Success);
        Assert.Equal(TransitionErrorCode.TerminalState, voided.Code);
    }

    // ------------------------------------------------------------------- the carrier seam

    [Fact]
    public void The_default_carrier_source_always_accepts()
    {
        var booking = new ConfiguredCarrierBooking(new ShippingConfiguration());

        var result = booking.Book(new CarrierBookingRequest(Guid.NewGuid(), UnitCount: 2));

        // RefusalRate defaults to 0.0 so the unattended pipeline flows; this asserts the
        // shipped default rather than a test-only one.
        Assert.Equal(CarrierBookingOutcome.Accepted, result.Outcome);
        Assert.Contains(result.Carrier, ShippingConfiguration.DefaultCarriers);
        Assert.False(string.IsNullOrWhiteSpace(result.TrackingNumber));
        Assert.NotNull(result.EstimatedArrivalUtc);
    }

    [Fact]
    public void Transit_days_feed_the_estimated_arrival()
    {
        var booking = new ConfiguredCarrierBooking(new ShippingConfiguration { TransitDays = 10 });

        var result = booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1));

        Assert.Equal(10, (result.EstimatedArrivalUtc!.Value - DateTime.UtcNow).TotalDays, precision: 1);
    }

    [Fact]
    public void A_visitor_may_name_a_carrier_and_is_told_when_it_is_not_one_of_ours()
    {
        var config = new ShippingConfiguration { Carriers = ["Ravenscroft Haulage", "Meridian Aether Post"] };
        var booking = new ConfiguredCarrierBooking(config);

        // Case-insensitive, because a visitor typing a name into Swagger shouldn't be punished
        // for it — but the configured spelling is what gets recorded.
        var chosen = booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1, "ravenscroft haulage"));
        Assert.Equal(CarrierBookingOutcome.Accepted, chosen.Outcome);
        Assert.Equal("Ravenscroft Haulage", chosen.Carrier);

        var unknown = booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1, "Definitely Not A Carrier"));
        Assert.Equal(CarrierBookingOutcome.UnknownCarrier, unknown.Outcome);
        Assert.Contains("Ravenscroft Haulage", unknown.Reason);
    }

    [Fact]
    public void A_refusing_carrier_refuses_and_says_why()
    {
        var config = new ShippingConfiguration
        {
            RefusalRate = 1.0,
            RefusalReason = "Every wagon is on the northern run.",
            Seed = 7
        };
        var booking = new ConfiguredCarrierBooking(config);

        var result = booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1));

        Assert.Equal(CarrierBookingOutcome.Refused, result.Outcome);
        Assert.Equal("Every wagon is on the northern run.", result.Reason);
        // No tracking number: a refusal is not half a booking, and 7.3 writes no row for it.
        Assert.Null(result.TrackingNumber);
    }

    [Fact]
    public void A_seed_makes_the_coin_flip_reproducible()
    {
        static List<CarrierBookingOutcome> Run() =>
            Enumerable.Range(0, 20)
                .Select(_ => new ConfiguredCarrierBooking(new ShippingConfiguration { RefusalRate = 0.5, Seed = 1234 }))
                .Take(1)
                .SelectMany(booking => Enumerable.Range(0, 20)
                    .Select(_ => booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1)).Outcome))
                .ToList();

        var first = Run();
        var second = Run();

        // Mixed outcomes, and the same mix twice — the only way to assert on a coin flip.
        Assert.Contains(CarrierBookingOutcome.Refused, first);
        Assert.Contains(CarrierBookingOutcome.Accepted, first);
        Assert.Equal(first, second);
    }
}
