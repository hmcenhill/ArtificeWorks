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
    NotHeld
}
