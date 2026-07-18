namespace ArtificeWorks.Domain.Models;

/// <summary>
/// Outcome of a work order lifecycle command. Carries the reason a transition
/// was rejected so callers can surface it instead of a silent <c>false</c>.
/// </summary>
public readonly record struct TransitionResult
{
    public bool Success { get; }
    public string? Error { get; }

    private TransitionResult(bool success, string? error)
    {
        Success = success;
        Error = error;
    }

    public static TransitionResult Ok() => new(true, null);
    public static TransitionResult Rejected(string reason) => new(false, reason);
}
