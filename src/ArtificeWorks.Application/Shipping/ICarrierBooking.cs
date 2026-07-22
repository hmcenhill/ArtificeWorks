namespace ArtificeWorks.Application.Shipping;

/// <summary>
/// Where a carrier booking comes from — the shipping stage's equivalent of
/// <see cref="Inspection.IVerdictSource"/>, and deliberately the same swappable shape.
/// </summary>
/// <remarks>
/// Behind an interface so Epic 10's simulation engine can make capacity depend on the simulated
/// world, and Epic 12 can force a refusal, without either touching the shipping workflow. The
/// same warning applies as to the verdict source: the moment an implementation wants to know
/// about routes, weights or depots, it has become Epic 10.
/// </remarks>
public interface ICarrierBooking
{
    CarrierBookingResult Book(CarrierBookingRequest request);
}

/// <param name="RequestedCarrier">
/// The carrier a visitor picked, or null to let the booking source choose. The manual endpoint
/// is the only caller that ever sets it — the unattended pipeline has no opinion.
/// </param>
public sealed record CarrierBookingRequest(
    Guid WorkOrderId,
    int UnitCount,
    string? RequestedCarrier = null);

public enum CarrierBookingOutcome
{
    Accepted,

    /// <summary>
    /// No capacity. An external constraint, not a defect — so the order goes OnHold with the
    /// reason and the message acks, exactly as an insufficient-stock hold does (5.3).
    /// </summary>
    Refused,

    /// <summary>A visitor named a carrier this factory does not work with. A bad request, not a conflict.</summary>
    UnknownCarrier
}

/// <param name="Carrier">The carrier that took the job. Set only when accepted.</param>
/// <param name="TrackingNumber">Set only when accepted.</param>
/// <param name="EstimatedArrivalUtc">Set only when accepted.</param>
/// <param name="Reason">Why the booking failed. Set unless accepted.</param>
public sealed record CarrierBookingResult(
    CarrierBookingOutcome Outcome,
    string? Carrier = null,
    string? TrackingNumber = null,
    DateTime? EstimatedArrivalUtc = null,
    string? Reason = null)
{
    public static CarrierBookingResult Accepted(string carrier, string trackingNumber, DateTime eta) =>
        new(CarrierBookingOutcome.Accepted, carrier, trackingNumber, eta);

    public static CarrierBookingResult Refused(string carrier, string reason) =>
        new(CarrierBookingOutcome.Refused, carrier, Reason: reason);

    public static CarrierBookingResult Unknown(string requestedCarrier, string reason) =>
        new(CarrierBookingOutcome.UnknownCarrier, requestedCarrier, Reason: reason);
}
