namespace ArtificeWorks.Domain.Models.Shipping;

/// <summary>
/// Where a parcel is in its own short life. Deliberately three values and no more: an
/// in-transit stage with a delivery confirmation would need a timer or a sweeper, and all
/// pacing belongs to Epic 10's simulation engine (settled at grooming).
/// </summary>
public enum ShipmentStatus
{
    /// <summary>A carrier has accepted the job. The parcel has not left the factory.</summary>
    Booked,

    /// <summary>Handed to the carrier. Terminal, and the transition that completes the order.</summary>
    Dispatched,

    /// <summary>
    /// Voided because the work order was cancelled before dispatch. A cancelled order must not
    /// leave a live parcel behind it; after dispatch the question cannot arise, because a
    /// Completed order refuses <c>Cancel</c> already.
    /// </summary>
    Cancelled
}
