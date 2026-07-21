namespace ArtificeWorks.Domain.Models.Production;

/// <summary>
/// The record of one production attempt on one work order — and the attempt's idempotency key.
/// <para>
/// Epic 5 got its dedupe for free: picking happens exactly once per order, so a unique index on
/// <c>material_reservations.WorkOrderId</c> answered "has this been done?" outright. Production
/// legitimately repeats, so that question no longer has an answer. The thing that <em>should</em>
/// happen exactly once is an <em>attempt</em>, so the key is <c>(WorkOrderId, AttemptNumber)</c>
/// and this row is what carries it (6.4).
/// </para>
/// <para>
/// It keeps 5.4's best property: the dedupe marker and the work commit together. This row, the
/// units it built, the order's status change, and its history entry are all written in a single
/// <c>SaveChanges</c>, so a duplicate attempt's unique-constraint violation rolls back the units
/// too. There is no separate inbox table that could drift from reality.
/// </para>
/// </summary>
public class ProductionRun
{
    public Guid Id { get; }
    public Guid WorkOrderId { get; }

    /// <summary>1 for the initial build, 2+ for each rebuild of a shortfall.</summary>
    public int AttemptNumber { get; }

    /// <summary>How many serialized units this attempt produced (the shortfall at the time).</summary>
    public uint UnitsBuilt { get; }

    public DateTime BuiltUtc { get; }

    private ProductionRun() { }

    public ProductionRun(Guid workOrderId, int attemptNumber, uint unitsBuilt)
    {
        if (attemptNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Attempt number must be greater than 0.");
        }

        Id = Guid.NewGuid();
        WorkOrderId = workOrderId;
        AttemptNumber = attemptNumber;
        UnitsBuilt = unitsBuilt;
        BuiltUtc = DateTime.UtcNow;
    }
}
