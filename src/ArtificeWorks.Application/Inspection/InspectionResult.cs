namespace ArtificeWorks.Application.Inspection;

/// <summary>How an inspection of one build attempt ended — and therefore what the order did next.</summary>
public enum InspectionOutcome
{
    /// <summary>The full ordered quantity has passed; the order advanced to Delivery.</summary>
    Passed,

    /// <summary>Units were scrapped; the order went back to InProcess to rebuild the shortfall.</summary>
    ReworkRequired,

    /// <summary>The rebuild cap was exceeded; the order was routed to Fault and the cycle stopped.</summary>
    Faulted,

    /// <summary>Units are in Inspection still waiting for a verdict — the manual-inspection path.</summary>
    AwaitingVerdicts,

    /// <summary>This attempt had already been inspected — a duplicate delivery, safely ignored.</summary>
    AlreadyInspected,

    /// <summary>No work order with that id.</summary>
    WorkOrderNotFound,

    /// <summary>The order could not be inspected in its current state (held, cancelled, faulted).</summary>
    Rejected
}

/// <param name="Summary">Human-readable description of what happened, as written to state history.</param>
public sealed record InspectionResult(
    InspectionOutcome Outcome,
    string Summary,
    int AttemptNumber,
    uint UnitsPassed = 0,
    uint UnitsScrapped = 0);

/// <summary>How recording a single verdict through the API ended.</summary>
public enum VerdictOutcome
{
    Recorded,
    WorkOrderNotFound,

    /// <summary>No unit with that serial number belongs to this work order.</summary>
    UnitNotFound,

    /// <summary>The order is not in Inspection, so its units are not up for judgement.</summary>
    NotInInspection,

    /// <summary>The unit already carries a verdict. The clean resolution of an auto/manual race.</summary>
    AlreadyInspected,

    /// <summary>A failing verdict arrived without a reason.</summary>
    ReasonRequired
}

/// <param name="OrderOutcome">What the order did as a result, if this verdict completed the attempt.</param>
public sealed record VerdictResult(
    VerdictOutcome Outcome,
    string Summary,
    InspectionOutcome? OrderOutcome = null);
