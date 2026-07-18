namespace ArtificeWorks.Domain.Models;

/// <summary>
/// Outcome of a work order lifecycle command. On rejection it carries both a
/// stable <see cref="TransitionErrorCode"/> (for callers to branch on) and a
/// human-readable <see cref="Error"/> message (for display / ProblemDetails detail).
/// </summary>
public readonly record struct TransitionResult
{
    public bool Success { get; }
    public TransitionErrorCode? Code { get; }
    public string? Error { get; }

    private TransitionResult(bool success, TransitionErrorCode? code, string? error)
    {
        Success = success;
        Code = code;
        Error = error;
    }

    public static TransitionResult Ok() => new(true, null, null);

    public static TransitionResult Rejected(TransitionErrorCode code, string reason)
        => new(false, code, reason);
}
