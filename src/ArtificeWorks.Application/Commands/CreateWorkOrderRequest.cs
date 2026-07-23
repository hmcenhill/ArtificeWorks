using System.ComponentModel.DataAnnotations;

using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Commands;

public class CreateWorkOrderRequest
{
    /// <summary>
    /// Who this order is for (10.3). Defaults to <see cref="WorkOrderOrigin.Visitor"/>, so a
    /// visitor with curl need not know the field exists and every pre-Epic-10 caller keeps
    /// producing real demand.
    /// <para>
    /// <strong>Self-declared, on purpose.</strong> The generator goes through the public API rather
    /// than writing to the table — the epic's own smell test — so it has to be able to say what it
    /// is. A visitor could lie and mark their order simulated; that is the same class of thing as
    /// being able to clear the board with <c>POST /system/world/reset</c>, and it is answered by
    /// the admin gate deferred since Epic 3, not by a second mechanism here.
    /// </para>
    /// </summary>
    public WorkOrderOrigin Origin { get; set; } = WorkOrderOrigin.Visitor;

    [Required]
    public required string Requestor { get; set; }

    [Required]
    public required string ItemId { get; set; }

    [Range(1, uint.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public uint Qty { get; set; }

    public string? Notes { get; set; }
}
