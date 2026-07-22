using System.ComponentModel.DataAnnotations;

namespace ArtificeWorks.Application.Commands;

/// <summary>
/// A hand-booked shipment — the visitor's decision moment in a stage that otherwise has none,
/// reachable when <c>Shipping:AutoBook</c> is off.
/// </summary>
public class BookShipmentRequest
{
    /// <summary>
    /// Which carrier to use. Omit it and the configured booking source picks one — the decision
    /// is offered, not demanded.
    /// </summary>
    public string? Carrier { get; set; }

    [Required]
    public required string CreatedBy { get; set; }
}
