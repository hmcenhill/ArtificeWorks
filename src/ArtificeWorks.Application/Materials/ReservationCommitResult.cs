using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Materials;

/// <summary>How a single attempt to commit a reservation ended.</summary>
public enum ReservationOutcome
{
    /// <summary>Every line was drawn and the reservation row is committed.</summary>
    Reserved,

    /// <summary>At least one component was short; nothing was drawn (all-or-nothing).</summary>
    InsufficientStock,

    /// <summary>This work order already has a reservation — a duplicate delivery.</summary>
    AlreadyReserved
}

/// <param name="ShortComponentIds">
/// The component(s) that couldn't be fully satisfied. Only the first shortage is detected —
/// the transaction aborts there — which is enough to name a reason on the hold.
/// </param>
public sealed record ReservationCommitResult(
    ReservationOutcome Outcome,
    MaterialReservation? Reservation = null,
    IReadOnlyList<string>? ShortComponentIds = null)
{
    public static ReservationCommitResult Reserved(MaterialReservation reservation)
        => new(ReservationOutcome.Reserved, reservation);

    public static ReservationCommitResult Short(IReadOnlyList<string> shortComponentIds)
        => new(ReservationOutcome.InsufficientStock, ShortComponentIds: shortComponentIds);

    public static ReservationCommitResult AlreadyReserved()
        => new(ReservationOutcome.AlreadyReserved);
}
