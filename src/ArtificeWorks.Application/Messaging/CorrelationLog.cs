using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// Shared correlation logging so the API and the worker stamp the id identically. Both call
/// <see cref="BeginCorrelationScope(ILogger, Guid)"/>/<see cref="BeginCorrelationScope(ILogger, string?)"/>,
/// which opens a message-template scope: it renders as <c>CorrelationId:{id}</c> on the console
/// (so one grep of a correlation id spans both services' logs) and exposes a structured
/// <c>CorrelationId</c> field to log backends (Epic 9). Centralising it here is what keeps the
/// field name — the thing every grep and query keys on — defined in exactly one place.
/// </summary>
public static class CorrelationLog
{
    // The placeholder name IS the structured field name; keep it identical on both services.
    private const string Template = "CorrelationId:{CorrelationId}";

    /// <summary>Opens the correlation log scope for an operation whose id is known (the API boundary).</summary>
    public static IDisposable? BeginCorrelationScope(this ILogger logger, Guid correlationId)
        => logger.BeginScope(Template, correlationId);

    /// <summary>
    /// Opens the correlation log scope from a wire value (the worker reads it off the AMQP
    /// <c>correlation_id</c> property). Returns null when no id is present, so callers can
    /// <c>using</c> it unconditionally.
    /// </summary>
    public static IDisposable? BeginCorrelationScope(this ILogger logger, string? correlationId)
        => string.IsNullOrEmpty(correlationId) ? null : logger.BeginScope(Template, correlationId);
}
