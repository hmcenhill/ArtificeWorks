using System.ComponentModel.DataAnnotations;

namespace ArtificeWorks.Application.Commands;

/// <summary>
/// A hand-recorded inspection verdict for one serialized unit — the human alternative to the
/// auto-inspector, and the path Epic 12's failure injection reuses.
/// </summary>
public class RecordVerdictRequest
{
    /// <summary>The unit being judged. Must belong to the work order in the route.</summary>
    [Required]
    public required Guid SerialNumber { get; set; }

    public required bool Passed { get; set; }

    /// <summary>Why the unit failed. Required when <see cref="Passed"/> is false.</summary>
    public string? Reason { get; set; }

    [Required]
    public required string CreatedBy { get; set; }
}
