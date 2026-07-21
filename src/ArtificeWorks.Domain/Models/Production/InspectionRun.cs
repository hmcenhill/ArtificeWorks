namespace ArtificeWorks.Domain.Models.Production;

/// <summary>
/// The record that one build attempt was presented for inspection — the mirror of
/// <see cref="ProductionRun"/>, keyed the same way and for the same reason.
/// <para>
/// The per-unit "already inspected" guard stops a redelivery re-verdicting individual units,
/// but it cannot by itself stop a redelivered <c>ProductionCompleted</c> from re-deciding the
/// <em>order-level</em> outcome — publishing a second <c>InspectionPassed</c>, or burning a
/// second rebuild attempt. The unique index on <c>(WorkOrderId, AttemptNumber)</c> does, and it
/// commits in the same <c>SaveChanges</c> as the verdicts and the resulting transition.
/// </para>
/// <para>
/// Note the row means "attempt N reached inspection", not "attempt N was auto-verdicted": with
/// the auto-inspector switched off the units wait for manual verdicts, and the run row is still
/// what makes the delivery idempotent.
/// </para>
/// </summary>
public class InspectionRun
{
    public Guid Id { get; }
    public Guid WorkOrderId { get; }

    /// <summary>The build attempt whose units this inspection judged.</summary>
    public int AttemptNumber { get; }

    public uint UnitsPassed { get; private set; }
    public uint UnitsScrapped { get; private set; }

    public DateTime InspectedUtc { get; }

    private InspectionRun() { }

    public InspectionRun(Guid workOrderId, int attemptNumber)
    {
        if (attemptNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Attempt number must be greater than 0.");
        }

        Id = Guid.NewGuid();
        WorkOrderId = workOrderId;
        AttemptNumber = attemptNumber;
        InspectedUtc = DateTime.UtcNow;
    }

    public void RecordVerdicts(uint passed, uint scrapped)
    {
        UnitsPassed = passed;
        UnitsScrapped = scrapped;
    }
}
