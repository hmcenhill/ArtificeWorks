using ArtificeWorks.Application.Chaos;

namespace ArtificeWorks.Infrastructure.Persistence;

/// <summary>
/// The row behind an <see cref="InjectedFault"/> (Epic 12).
/// <para>
/// Plumbing rather than domain — it describes a fault a visitor <em>injected</em>, not anything the
/// factory makes — so it lives here next to <c>SimulationSettingsRow</c> and <c>DeadLetter</c>
/// rather than in Domain with the aggregates. It is durable and cross-process on purpose: the API
/// arms it, and the worker (a different host) fires it, so an in-memory flag in one process would be
/// invisible to the other.
/// </para>
/// </summary>
public class InjectedFaultRow
{
    public Guid Id { get; private set; }

    /// <summary>The one order this fault targets — the bounded blast radius, indexed for the pipeline's lookup.</summary>
    public Guid WorkOrderId { get; private set; }

    public InjectedFaultKind Kind { get; private set; }

    public DateTime ArmedUtc { get; private set; }

    public string ArmedBy { get; private set; } = string.Empty;

    /// <summary>
    /// When the pipeline fired this fault, or null while it is still armed. A soft marker rather than
    /// a delete, so 12.3 can show that an order was broken on purpose, and so "fires once" is a
    /// <c>WHERE FiredUtc IS NULL</c> away.
    /// </summary>
    public DateTime? FiredUtc { get; set; }

    private InjectedFaultRow() { }

    public static InjectedFaultRow Arm(Guid workOrderId, InjectedFaultKind kind, string armedBy) => new()
    {
        Id = Guid.NewGuid(),
        WorkOrderId = workOrderId,
        Kind = kind,
        ArmedUtc = DateTime.UtcNow,
        ArmedBy = armedBy,
        FiredUtc = null,
    };

    public InjectedFault ToFault() => new(Id, WorkOrderId, Kind, ArmedUtc, ArmedBy, FiredUtc);
}
