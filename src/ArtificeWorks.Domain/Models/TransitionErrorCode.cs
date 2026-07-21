namespace ArtificeWorks.Domain.Models;

/// <summary>
/// Stable discriminator for why a work order lifecycle command was rejected.
/// The API maps each value onto a machine-readable ProblemDetails reason code;
/// callers program against these, not the human-readable message.
/// </summary>
public enum TransitionErrorCode
{
    /// <summary>The order is in a terminal state (Completed/Cancelled) and accepts no further commands.</summary>
    TerminalState,

    /// <summary>The order is held or faulted and must be released before it can advance.</summary>
    MustReleaseFirst,

    /// <summary>The order is already held (or faulted) and cannot be held again.</summary>
    AlreadyHeld,

    /// <summary>The order is not currently held, so there is nothing to release.</summary>
    NotHeld,

    /// <summary>The command is not legal from the order's current status (e.g. returning an
    /// order to production when it is not in Inspection).</summary>
    InvalidTransition,

    /// <summary>
    /// A production attempt arrived out of order — the requested attempt number is not the
    /// one that comes next. Almost always a redelivered rework event; the unique constraint
    /// on the production run is the actual guarantee, this is the cheap guard in front of it.
    /// </summary>
    AttemptOutOfSequence,

    /// <summary>The serialized unit already carries a verdict and cannot be inspected again.</summary>
    AlreadyInspected
}
