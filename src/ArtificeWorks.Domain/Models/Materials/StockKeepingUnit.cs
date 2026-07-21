namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// One serialized finished automaton, built to order.
/// <para>
/// Until Epic 6 this was a decorative record — a serial number and a product. It is now the
/// thing inspection actually judges: it carries its own status, the build attempt that
/// produced it, and (if it failed) why it was scrapped. A unit belongs to exactly one work
/// order for its whole life; this factory builds to order rather than allocating finished
/// units off a shelf.
/// </para>
/// </summary>
public class StockKeepingUnit
{
    public Product Product { get; }
    public Guid SerialNumber { get; }

    public UnitStatus Status { get; private set; }
    public DateTime BuiltUtc { get; }

    /// <summary>When a verdict was recorded. Null while the unit is still <see cref="UnitStatus.Built"/>.</summary>
    public DateTime? InspectedUtc { get; private set; }

    /// <summary>Why the unit failed inspection. Null unless <see cref="UnitStatus.Scrapped"/>.</summary>
    public string? ScrapReason { get; private set; }

    /// <summary>
    /// Which production attempt on the owning work order built this unit (1 for the initial
    /// build, 2+ for rebuilds). This is what lets a rebuild inspect only the units it just
    /// made, and what makes an attempt an addressable thing for idempotency (6.4).
    /// </summary>
    public int BuildAttempt { get; }

    private StockKeepingUnit() { }

    public StockKeepingUnit(Product product, int buildAttempt = 1)
    {
        if (buildAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(buildAttempt), "Build attempt must be greater than 0.");
        }

        Product = product;
        SerialNumber = Guid.NewGuid();
        Status = UnitStatus.Built;
        BuiltUtc = DateTime.UtcNow;
        BuildAttempt = buildAttempt;
    }

    /// <summary>Records a passing verdict. A unit may only be inspected once.</summary>
    public TransitionResult Pass() => Verdict(UnitStatus.Passed, scrapReason: null);

    /// <summary>Records a failing verdict with its reason. A unit may only be inspected once.</summary>
    public TransitionResult Scrap(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return TransitionResult.Rejected(TransitionErrorCode.InvalidTransition,
                "A scrapped unit must carry a reason.");
        }
        return Verdict(UnitStatus.Scrapped, reason);
    }

    /// <summary>
    /// The double-verdict guard. Both verdict paths — the auto-inspector on the consumer and
    /// the manual API endpoint — land here, so "a unit cannot be inspected twice" holds no
    /// matter which of them (or which redelivery) arrives second.
    /// </summary>
    private TransitionResult Verdict(UnitStatus outcome, string? scrapReason)
    {
        if (Status != UnitStatus.Built)
        {
            return TransitionResult.Rejected(TransitionErrorCode.AlreadyInspected,
                $"Unit {SerialNumber} was already inspected at {InspectedUtc:O} and is {Status}.");
        }

        Status = outcome;
        ScrapReason = scrapReason;
        InspectedUtc = DateTime.UtcNow;
        return TransitionResult.Ok();
    }
}
