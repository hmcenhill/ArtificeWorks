namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// The record of a completed pick: the components (and quantities) drawn off the shelf for
/// one <em>build attempt</em> of a work order. Modelled as its own aggregate rather than as
/// state on <see cref="WorkOrder"/>, because a work order's <c>AssignedStock</c> is about
/// <em>finished serialized units</em> (Epic 6) while this is about <em>input materials consumed
/// to build them</em> — conflating the two would muddle both.
/// <para>
/// It is also the picking stage's idempotency key: exactly one reservation may exist per
/// <c>(WorkOrderId, AttemptNumber)</c> pair (enforced by a unique index), so a redelivered
/// scheduling or rework event's insert collides instead of double-picking. The dedupe marker
/// and the reservation are literally the same row, which makes their atomicity free.
/// </para>
/// <para>
/// The key was order-scoped until 13.1. It widened because a rebuild physically burns parts:
/// attempt 2 draws the outstanding quantity again, so "one pick per order" stopped being true
/// before it stopped being enforceable.
/// </para>
/// </summary>
public class MaterialReservation
{
    public Guid Id { get; }
    public Guid WorkOrderId { get; }

    /// <summary>
    /// Which build attempt this pick supplies — 1 for the initial pick, N+1 for the rebuild that
    /// follows a failed attempt N. Derived by the caller from the event that triggered the pick
    /// (never read from the order's current state), so a redelivery computes the same number and
    /// collides on the unique key rather than drawing stock twice.
    /// </summary>
    public int AttemptNumber { get; }

    public DateTime ReservedUtc { get; }

    public IReadOnlyList<MaterialReservationLine> Lines => _lines.AsReadOnly();
    private readonly List<MaterialReservationLine> _lines = new();

    private MaterialReservation() { }

    public MaterialReservation(Guid workOrderId, int attemptNumber, IEnumerable<ComponentDemand> demand)
    {
        if (attemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Attempt numbers start at 1.");
        }

        Id = Guid.NewGuid();
        WorkOrderId = workOrderId;
        AttemptNumber = attemptNumber;
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

    /// <summary>
    /// Human-readable summary for the work order's state history and its timeline.
    /// <para>
    /// The attempt appears only from the second pick onwards. An order can now have several picks,
    /// and two of them can draw an identical set of lines — so without the attempt the timeline
    /// would show the same sentence twice with no way to tell which rebuild it belonged to. The
    /// initial pick keeps its original wording because there is nothing to disambiguate it from.
    /// </para>
    /// </summary>
    public string Describe()
    {
        var lines = string.Join(", ", _lines.Select(line => $"{line.Quantity}× {line.ComponentId}"));
        return AttemptNumber == 1 ? lines : $"{lines} (rebuild attempt {AttemptNumber})";
    }
}
