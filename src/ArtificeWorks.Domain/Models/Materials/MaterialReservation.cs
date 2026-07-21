namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// The record of a completed pick: the components (and quantities) drawn off the shelf for
/// one work order. Modelled as its own aggregate rather than as state on <see cref="WorkOrder"/>,
/// because a work order's <c>AssignedStock</c> is about <em>finished serialized units</em>
/// (Epic 6) while this is about <em>input materials consumed to build them</em> — conflating
/// the two would muddle both.
/// <para>
/// It is also the epic's idempotency key: exactly one reservation may exist per work order
/// (enforced by a unique index on <see cref="WorkOrderId"/>), so a redelivered scheduling
/// event's insert collides instead of double-picking. The dedupe marker and the reservation
/// are literally the same row, which makes their atomicity free.
/// </para>
/// </summary>
public class MaterialReservation
{
    public Guid Id { get; }
    public Guid WorkOrderId { get; }
    public DateTime ReservedUtc { get; }

    public IReadOnlyList<MaterialReservationLine> Lines => _lines.AsReadOnly();
    private readonly List<MaterialReservationLine> _lines = new();

    private MaterialReservation() { }

    public MaterialReservation(Guid workOrderId, IEnumerable<ComponentDemand> demand)
    {
        Id = Guid.NewGuid();
        WorkOrderId = workOrderId;
        ReservedUtc = DateTime.UtcNow;

        foreach (var line in demand)
        {
            _lines.Add(new MaterialReservationLine(this, line.ComponentId, line.Quantity));
        }

        if (_lines.Count == 0)
        {
            throw new ArgumentException("A reservation must reserve at least one component line.", nameof(demand));
        }
    }

    /// <summary>Human-readable summary for the work order's state history.</summary>
    public string Describe() => string.Join(", ", _lines.Select(line => $"{line.Quantity}× {line.ComponentId}"));
}
