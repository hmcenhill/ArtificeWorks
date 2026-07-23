using System.Net;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Infrastructure.Observability;

/// <summary>
/// A health signal for a host that is not a web application (9.4, shared across hosts by 10.1).
/// The worker had none, and it is the half more likely to be wedged: the API failing is loud, a
/// consumer that has quietly stopped consuming is not. The simulation host has the same problem and
/// less to notice it by, so it uses the same listener.
/// <para>
/// <strong>Deliberately an <see cref="HttpListener"/>, not a web host.</strong> Making the worker a
/// Web SDK project to expose two endpoints would give it Kestrel, MVC's conventions, a second
/// startup pipeline and a second set of ports to reason about, for a feature that is fifty lines.
/// The story asked for something kept small; this is that.
/// </para>
/// <para>
/// <strong>It cannot take its host down.</strong> A prefix it isn't allowed to bind, or a port
/// already in use, is a warning at startup and nothing more — the same rule the epic sets for
/// telemetry, for the same reason: the thing that watches the pipeline must never be able to stop it.
/// </para>
/// <list type="bullet">
///   <item><c>/health/live</c> — the process is up. No dependency checks at all.</item>
///   <item><c>/health/ready</c> — the same dependency checks the API runs.</item>
/// </list>
/// </summary>
public sealed class MinimalHealthEndpoint : BackgroundService
{
    /// <summary>Where to listen. Localhost by default because that needs no URL ACL on Windows.</summary>
    public const string PrefixSetting = "WorkerHealth:Prefix";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MinimalHealthEndpoint> _logger;

    private HttpListener? _listener;

    public MinimalHealthEndpoint(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<MinimalHealthEndpoint> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prefix = _configuration[PrefixSetting];
        if (string.IsNullOrWhiteSpace(prefix))
        {
            _logger.LogDebug("No {Setting} configured; this host exposes no health endpoint.", PrefixSetting);
            return;
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Could not listen on {Prefix}; this host will run without a health endpoint.", prefix);
            return;
        }

        _logger.LogInformation("Health endpoint listening on {Prefix} (/health/live, /health/ready).", prefix);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug(e, "Health listener accept failed; continuing.");
                continue;
            }

            // Fire and forget: a slow health check must not queue behind another one, and this
            // loop's only job is to keep accepting.
            _ = RespondAsync(context, stoppingToken);
        }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            var (status, body) = path switch
            {
                // Liveness checks NOTHING, on purpose. A failing liveness probe means "restart me",
                // and restarting the worker does not fix a dead database — it makes it worse, by
                // adding a reconnect storm to whatever is already struggling.
                "/health/live" => (HttpStatusCode.OK, "{\"status\":\"Healthy\"}"),
                "/health/ready" or "/health" => await ReadinessAsync(cancellationToken),
                _ => (HttpStatusCode.NotFound, "{\"status\":\"Unknown endpoint\"}")
            };

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Failed to answer a health request.");
        }
        finally
        {
            try { context.Response.Close(); } catch { /* the caller hung up */ }
        }
    }

    private async Task<(HttpStatusCode, string)> ReadinessAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var checks = scope.ServiceProvider.GetRequiredService<HealthCheckService>();

        var report = await checks.CheckHealthAsync(
            registration => registration.Tags.Contains(HealthChecks.ReadyTag), cancellationToken);

        // Degraded is a 200: an outbox backlog means the broker is unwell, not that this worker
        // should be taken out of rotation. See OutboxLagHealthCheck.
        return (report.Status == HealthStatus.Unhealthy
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.OK, HealthReportJson.Serialize(report));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        _listener?.Close();
        _listener = null;
    }
}
