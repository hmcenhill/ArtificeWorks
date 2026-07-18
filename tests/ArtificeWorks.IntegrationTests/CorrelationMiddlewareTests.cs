using ArtificeWorks.Api.Middleware;
using ArtificeWorks.Application.Messaging;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// Fast unit-style tests for the correlation edge (no broker/DB): the middleware adopts a
/// valid inbound id or generates one, echoes it on the response, and — the 4.3 addition —
/// opens a logging scope carrying that id under the shared <see cref="CorrelationLog.ScopeKey"/>
/// so every log line the request produces is greppable by it.
/// </summary>
public class CorrelationMiddlewareTests
{
    [Fact]
    public async Task Adopts_valid_inbound_header_and_echoes_it()
    {
        var incoming = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = incoming.ToString();
        var correlation = new CorrelationContext();

        await InvokeAsync(context, correlation, new RecordingLogger());

        Assert.Equal(incoming, correlation.CorrelationId);
        Assert.Equal(incoming.ToString(), context.Response.Headers[CorrelationMiddleware.HeaderName]);
    }

    [Fact]
    public async Task Keeps_generated_id_when_inbound_header_is_absent_or_invalid()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "not-a-guid";
        var correlation = new CorrelationContext();
        var generated = correlation.CorrelationId;

        await InvokeAsync(context, correlation, new RecordingLogger());

        Assert.Equal(generated, correlation.CorrelationId);
        Assert.Equal(generated.ToString(), context.Response.Headers[CorrelationMiddleware.HeaderName]);
    }

    [Fact]
    public async Task Opens_a_log_scope_carrying_the_correlation_id()
    {
        var incoming = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = incoming.ToString();
        var correlation = new CorrelationContext();
        var logger = new RecordingLogger();

        await InvokeAsync(context, correlation, logger);

        // The scope is a message-template state exposing a structured "CorrelationId" field.
        var scope = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object>>>(Assert.Single(logger.Scopes));
        var field = Assert.Single(scope, p => p.Key == "CorrelationId");
        Assert.Equal(incoming, field.Value);
        Assert.True(logger.ScopeWasOpenDuringNext, "the scope must wrap the downstream pipeline");
    }

    private static Task InvokeAsync(HttpContext context, CorrelationContext correlation, RecordingLogger logger)
    {
        var middleware = new CorrelationMiddleware(
            next: _ =>
            {
                logger.ScopeWasOpenDuringNext = logger.OpenScopes > 0;
                return Task.CompletedTask;
            },
            logger: logger);

        return middleware.InvokeAsync(context, correlation);
    }

    /// <summary>Captures the state objects passed to <c>BeginScope</c> and whether a scope is open.</summary>
    private sealed class RecordingLogger : ILogger<CorrelationMiddleware>
    {
        public List<object> Scopes { get; } = [];
        public int OpenScopes { get; private set; }
        public bool ScopeWasOpenDuringNext { get; set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state);
            OpenScopes++;
            return new ScopeToken(this);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        { }

        private sealed class ScopeToken(RecordingLogger owner) : IDisposable
        {
            public void Dispose() => owner.OpenScopes--;
        }
    }
}
