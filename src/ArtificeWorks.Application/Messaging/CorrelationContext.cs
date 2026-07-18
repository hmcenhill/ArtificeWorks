namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// Default mutable, scoped holder for the correlation id. The API boundary sets it from
/// the inbound <c>X-Correlation-ID</c> header (or leaves the generated default); the
/// publisher reads it. One instance per scope (per request), so the default is a fresh id.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
}
