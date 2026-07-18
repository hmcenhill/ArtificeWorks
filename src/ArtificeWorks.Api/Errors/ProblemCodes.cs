using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Api.Errors;

/// <summary>
/// The stable, machine-readable reason codes carried in the <c>code</c> extension
/// of every non-2xx ProblemDetails response. These are part of the public API
/// contract: consumers branch on them, so the string values must not change.
/// </summary>
public static class ProblemCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string WorkOrderNotFound = "work_order_not_found";
    public const string ProductNotFound = "product_not_found";
    public const string ProductAlreadyExists = "product_already_exists";
    public const string TerminalState = "terminal_state";
    public const string MustReleaseFirst = "must_release_first";
    public const string AlreadyHeld = "already_held";
    public const string NotHeld = "not_held";
    public const string InternalError = "internal_error";

    /// <summary>Maps a domain transition-rejection code onto its wire reason code.</summary>
    public static string ForTransition(TransitionErrorCode code) => code switch
    {
        TransitionErrorCode.TerminalState => TerminalState,
        TransitionErrorCode.MustReleaseFirst => MustReleaseFirst,
        TransitionErrorCode.AlreadyHeld => AlreadyHeld,
        TransitionErrorCode.NotHeld => NotHeld,
        _ => InternalError
    };
}
