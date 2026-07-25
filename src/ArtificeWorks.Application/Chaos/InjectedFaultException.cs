namespace ArtificeWorks.Application.Chaos;

/// <summary>
/// Thrown by a pipeline stage when it fires one of the two broker-facing injected faults (12.2).
/// <para>
/// It is not a real failure — a visitor armed it — but it is deliberately <em>indistinguishable</em>
/// from a real one to the consumer, so recovery runs on Epic 8's existing paths with no "chaos mode"
/// branch. The consumer classifies it by <see cref="Kind"/> exactly as it classifies a genuine throw:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="InjectedFaultKind.TransientOnce"/> is an ordinary throw. The
///     consumer's transient branch pushes it onto the retry ladder, and the redelivery — with the
///     fault now disarmed — completes the stage.</description></item>
///   <item><description><see cref="InjectedFaultKind.Poison"/> asks the consumer to park immediately,
///     the same as a real <c>PoisonMessageException</c>: it becomes a <c>dead_letters</c> row awaiting
///     a human replay.</description></item>
/// </list>
/// </summary>
public sealed class InjectedFaultException : Exception
{
    public InjectedFaultException(InjectedFaultKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    /// <summary>Which broker fault fired — the consumer routes transient to the ladder, poison to the parked queue.</summary>
    public InjectedFaultKind Kind { get; }
}
