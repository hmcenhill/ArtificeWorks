using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ArtificeWorks.Api.Middleware;

/// <summary>
/// Marks an endpoint as honouring the <c>Idempotency-Key</c> header (8.4). Extending the
/// mechanism to another endpoint is this attribute and nothing else — the filter is entirely
/// indifferent to what it wraps.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : TypeFilterAttribute
{
    public IdempotentAttribute() : base(typeof(IdempotencyFilter))
    {
    }
}

/// <summary>
/// Makes a client retry harmless: the same <c>Idempotency-Key</c> twice produces one work order
/// and one <c>201</c>, not two orders.
/// <para>
/// This is the one edge the system does not control. Everything downstream is already safe
/// against repetition — 5.4's reservation index, 6.4's run tables, 7.1's shipment index, and the
/// state machines in between — and 8.1 made the events themselves at-least-once on purpose,
/// betting on exactly that groundwork. An HTTP client that never learns whether its request
/// landed was the last unguarded door.
/// </para>
/// <para>
/// <strong>The row is staged, not saved.</strong> It is added to the request's own
/// <see cref="ArtificeWorksDbContext"/> before the action runs, so the action's
/// <c>SaveChanges</c> commits the work order, its outbox row <em>and</em> the key together. One
/// transaction holding the work, its announcement and the marker that says it happened — which is
/// the whole epic in a single commit. A crash between them cannot make the guarantee a lie,
/// because there is no "between them".
/// </para>
/// <para>
/// <strong>The unique index is the guarantee, not the pre-check read.</strong> Two simultaneous
/// requests with one key both pass the lookup; the loser's <c>SaveChanges</c> is rejected by the
/// database and its whole transaction — order included — rolls back. It then waits briefly for
/// the winner to record its response and replays that. Same check-then-act shape 5.4 established,
/// with the constraint doing the real work.
/// </para>
/// </summary>
public sealed class IdempotencyFilter : IAsyncResourceFilter
{
    public const string HeaderName = "Idempotency-Key";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ArtificeWorksDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IdempotencyFilter> _logger;

    public IdempotencyFilter(
        ArtificeWorksDbContext context,
        IServiceScopeFactory scopeFactory,
        ILogger<IdempotencyFilter> logger)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        // No header → behave exactly as before. The header is opt-in: a visitor with curl should
        // not need to know it exists, and the existing suite proves that by not knowing.
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var header)
            || string.IsNullOrWhiteSpace(header.ToString()))
        {
            await next();
            return;
        }

        var key = header.ToString();
        var endpoint = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        var requestHash = await HashBodyAsync(context.HttpContext);

        var existing = await _context.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(record => record.Key == key, context.HttpContext.RequestAborted);

        if (existing is not null)
        {
            context.Result = ResultFor(existing, requestHash, key);
            return;
        }

        var staged = new IdempotencyRecord(key, endpoint, requestHash, DateTime.UtcNow);
        _context.IdempotencyKeys.Add(staged);

        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            if (Unwrap(executed.Exception) is DuplicateKeyException)
            {
                // Somebody else committed this key while we were working, taking our order down
                // with the rollback. Answer with their response, not an error: from the client's
                // point of view its request succeeded exactly once, which is what it asked for.
                //
                // The replay is written here rather than handed back as `executed.Result`. A
                // resource filter wraps *result execution*, so this post-next() code runs after
                // the pipeline is already past it — a result assigned now is never executed, and
                // the loser of the race gets a bare 200 with no body. (The short-circuit above is
                // different: `ResourceExecutingContext.Result` runs instead of the action, so the
                // ordinary replay path can and does hand its result back.)
                var replay = await ReplayWinnerAsync(key, requestHash, context.HttpContext.RequestAborted);
                await replay.ExecuteResultAsync(new ActionContext(
                    context.HttpContext, context.RouteData, context.ActionDescriptor));

                executed.ExceptionHandled = true;

                // The response is already written; leave nothing behind that could write it twice.
                executed.Result = new EmptyResult();
            }

            // Anything else is a real failure; let it reach the exception handler untouched. The
            // key row is still merely tracked, so it dies with the request and nothing is pinned.
            return;
        }

        await RecordResponseAsync(staged, executed, context.HttpContext.RequestAborted);
    }

    /// <summary>
    /// Captures the response so a retry can be answered with it verbatim. Only successful ones:
    /// pinning a <c>400</c> to a key the client will sensibly retry with a corrected body would
    /// turn a typo into a permanent rejection.
    /// </summary>
    private async Task RecordResponseAsync(
        IdempotencyRecord staged, ResourceExecutedContext executed, CancellationToken cancellationToken)
    {
        var status = StatusOf(executed.Result);

        if (status is < 200 or >= 300)
        {
            // The action almost certainly never saved, so the row is still merely tracked; detach
            // it. If it somehow did commit, remove it — either way the key is free again.
            var entry = _context.Entry(staged);
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else
            {
                _context.IdempotencyKeys.Remove(staged);
                await _context.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var (body, location) = BodyAndLocationOf(executed);
        staged.RecordResponse(status, body, location, DateTime.UtcNow);

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Stored idempotency key {Key} for {Endpoint} with a {Status} response.",
            staged.Key, staged.Endpoint, status);
    }

    /// <summary>
    /// What to do about a key we have seen before: replay it, reject it, or admit we don't know
    /// yet.
    /// </summary>
    private IActionResult ResultFor(IdempotencyRecord existing, string requestHash, string key)
    {
        if (existing.RequestHash != requestHash)
        {
            // Same key, different body. That is a client bug, and silently replaying the first
            // response would hide it. 422 rather than 409 because nothing conflicts with the
            // resource's state — the request contradicts itself.
            _logger.LogWarning("Idempotency key {Key} was reused with a different request body.", key);

            return Problem(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReused,
                $"Idempotency key '{key}' was already used for a different request body.");
        }

        if (!existing.IsComplete)
        {
            // The key is claimed but the original request has not finished (or died before it
            // could record its answer). We genuinely do not know the outcome, and inventing one
            // would be worse than saying so.
            return Problem(StatusCodes.Status409Conflict, ProblemCodes.IdempotencyKeyInFlight,
                $"A request with idempotency key '{key}' is still in flight. Retry shortly.");
        }

        _logger.LogInformation("Replaying stored response for idempotency key {Key}.", key);
        return StoredResult(existing);
    }

    /// <summary>
    /// The loser of a simultaneous race. The winner records its response immediately after its
    /// commit, so a brief bounded wait resolves it; failing that we admit the request is in
    /// flight rather than guessing.
    /// </summary>
    private async Task<IActionResult> ReplayWinnerAsync(string key, string requestHash, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Idempotency key {Key} was committed concurrently; replaying the winning request's response.", key);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            // A fresh scope: our own context is poisoned by the failed SaveChanges.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

            var winner = await context.IdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(record => record.Key == key, cancellationToken);

            if (winner is not null && (winner.IsComplete || winner.RequestHash != requestHash))
            {
                return ResultFor(winner, requestHash, key);
            }

            await Task.Delay(50, cancellationToken);
        }

        return Problem(StatusCodes.Status409Conflict, ProblemCodes.IdempotencyKeyInFlight,
            $"A request with idempotency key '{key}' is still in flight. Retry shortly.");
    }

    /// <summary>
    /// Replays the stored response byte for byte — status, body and <c>Location</c>. Storing the
    /// response rather than a bare "already seen" marker is what leaves the client correct: a
    /// caller that never received the first <c>201</c> has no other way to learn its order's id,
    /// and a <c>409</c> would strand it.
    /// </summary>
    private static IActionResult StoredResult(IdempotencyRecord record) => new StoredResponseResult(record);

    private static ObjectResult Problem(int statusCode, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Detail = detail,
            Title = code,
        };
        problem.Extensions["code"] = code;

        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>The MVC pipeline may hand back the exception wrapped; look one layer down.</summary>
    private static Exception Unwrap(Exception exception) =>
        exception is AggregateException aggregate ? aggregate.InnerException ?? exception : exception;

    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult statusResult => statusResult.StatusCode,
        _ => StatusCodes.Status200OK
    };

    private static (string? Body, string? Location) BodyAndLocationOf(ResourceExecutedContext executed)
    {
        var body = executed.Result switch
        {
            ObjectResult { Value: not null } objectResult
                => JsonSerializer.Serialize(objectResult.Value, SerializerOptions),
            _ => null
        };

        var location = executed.Result is CreatedResult created ? created.Location : null;
        return (body, location);
    }

    /// <summary>
    /// SHA-256 of the raw request body. Buffering is required so the model binder downstream can
    /// still read it — hence <c>EnableBuffering</c> and the rewind.
    /// </summary>
    private static async Task<string> HashBodyAsync(HttpContext http)
    {
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;

        using var memory = new MemoryStream();
        await http.Request.Body.CopyToAsync(memory);
        http.Request.Body.Position = 0;

        return Convert.ToHexStringLower(SHA256.HashData(memory.ToArray()));
    }

    /// <summary>Writes a stored response back out exactly as it was first sent.</summary>
    private sealed class StoredResponseResult : IActionResult
    {
        private readonly IdempotencyRecord _record;

        public StoredResponseResult(IdempotencyRecord record)
        {
            _record = record;
        }

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = _record.StatusCode ?? StatusCodes.Status200OK;

            if (!string.IsNullOrEmpty(_record.ResponseLocation))
            {
                response.Headers.Location = _record.ResponseLocation;
            }

            // Says out loud that this is a replay, not a fresh effect. Costs nothing and saves
            // somebody an afternoon.
            response.Headers["Idempotency-Replayed"] = "true";

            if (_record.ResponseBody is not null)
            {
                response.ContentType = "application/json";
                await response.WriteAsync(_record.ResponseBody, Encoding.UTF8);
            }
        }
    }
}
