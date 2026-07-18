using ArtificeWorks.Application.Messaging;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Api.Middleware;

/// <summary>
/// Establishes the correlation id for the request: honours an inbound
/// <c>X-Correlation-ID</c> header when it is a valid Guid, otherwise keeps the fresh id
/// the scoped <see cref="CorrelationContext"/> defaults to. Echoes it back on the
/// response so a caller can correlate too, and opens a logging scope carrying the id so
/// every log line the request produces is greppable by that one correlation id (4.3).
/// </summary>
public sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationMiddleware> _logger;

    public CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var header)
            && Guid.TryParse(header.ToString(), out var incoming))
        {
            correlation.CorrelationId = incoming;
        }

        context.Response.Headers[HeaderName] = correlation.CorrelationId.ToString();

        // Push the id into the logging scope so the API's own logs — and anything else that
        // runs within the request — carry it. The publisher stamps the same id onto every
        // event it emits, letting the worker pick the thread back up on the other side.
        using (_logger.BeginCorrelationScope(correlation.CorrelationId))
        {
            await _next(context);
        }
    }
}
