using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Application.Inspection;

/// <summary>Where an automatic inspection verdict comes from.</summary>
/// <remarks>
/// Behind an interface so Epic 10's simulation engine can supply verdicts that depend on the
/// simulated factory, and Epic 12 can force a failure, without either of them touching the
/// inspection workflow. Resist making an implementation clever: the moment it wants to know
/// about component quality, operator skill, or machine wear, it has become Epic 10.
/// </remarks>
public interface IVerdictSource
{
    UnitVerdict Verdict(StockKeepingUnit unit);
}

/// <param name="Reason">Why the unit failed. Required when <paramref name="Passed"/> is false.</param>
public sealed record UnitVerdict(bool Passed, string? Reason = null);
