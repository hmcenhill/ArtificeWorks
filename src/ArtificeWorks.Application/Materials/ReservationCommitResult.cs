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

/// <summary>
/// One component the pick could not fully satisfy, with the numbers that describe the gap.
/// <para>
/// <see cref="OnHand"/> is what the shelf held when the draw failed — a snapshot, not a claim: the
/// reservation transaction rolled back, so nothing is holding that stock and another order may take
/// it a moment later. It is honest enough for the two things it is used for: naming the shortage in
/// the hold's reason, and sizing the sub-assembly order 13.3 spawns. A child that turns out to build
/// slightly too few is corrected by the parent's next re-pick, which is the same self-correction the
/// rest of that loop relies on.
/// </para>
/// </summary>
/// <param name="Demanded">What this pick asked for.</param>
/// <param name="OnHand">What was on the shelf when the conditional decrement refused.</param>
public sealed record ShortComponent(string ComponentId, uint Demanded, uint OnHand)
{
    /// <summary>How many more are needed than exist — never zero, since this is a shortage.</summary>
    public uint Shortfall => Demanded > OnHand ? Demanded - OnHand : 0;

    public override string ToString() => $"{ComponentId} (need {Demanded}, have {OnHand})";
}

/// <param name="ShortComponents">
/// <strong>Every</strong> component that couldn't be satisfied, not just the first. Until 13.3 the
/// draw aborted at the first shortage, which was enough to name a reason on a hold — but a parent
/// that spawns one child per short made component needs the whole list, or it would discover the
/// second shortage only after the first child had run all the way to completion. The loop still
/// rolls everything back; it just finishes counting first.
/// </param>
public sealed record ReservationCommitResult(
    ReservationOutcome Outcome,
    MaterialReservation? Reservation = null,
    IReadOnlyList<ShortComponent>? ShortComponents = null)
{
    /// <summary>Just the ids, for the places that only want to name what is missing.</summary>
    public IReadOnlyList<string> ShortComponentIds =>
        ShortComponents?.Select(component => component.ComponentId).ToList() ?? [];

    public static ReservationCommitResult Reserved(MaterialReservation reservation)
        => new(ReservationOutcome.Reserved, reservation);

    public static ReservationCommitResult Short(IReadOnlyList<ShortComponent> shortComponents)
        => new(ReservationOutcome.InsufficientStock, ShortComponents: shortComponents);

    public static ReservationCommitResult AlreadyReserved()
        => new(ReservationOutcome.AlreadyReserved);
}
