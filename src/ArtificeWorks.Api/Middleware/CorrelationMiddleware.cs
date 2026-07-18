using ArtificeWorks.Application.Messaging;

namespace ArtificeWorks.Api.Middleware;

/// <summary>
/// Establishes the correlation id for the request: honours an inbound
/// <c>X-Correlation-ID</c> header when it is a valid Guid, otherwise keeps the fresh id
/// the scoped <see cref="CorrelationContext"/> defaults to. Echoes it back on the
/// response so a caller can correlate too. Story 4.3 extends this into log scopes.
/// </summary>
public sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var header)
            && Guid.TryParse(header.ToString(), out var incoming))
        {
            correlation.CorrelationId = incoming;
        }

        context.Response.Headers[HeaderName] = correlation.CorrelationId.ToString();

        await _next(context);
    }
}
