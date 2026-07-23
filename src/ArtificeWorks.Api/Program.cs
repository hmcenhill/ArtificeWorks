using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using ArtificeWorks.Api.Configuration;
using ArtificeWorks.Api.Errors;
using ArtificeWorks.Api.Middleware;
using ArtificeWorks.Api.Realtime;
using ArtificeWorks.Application.Handlers;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Data;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Simulation;
using ArtificeWorks.Infrastructure.Workflow;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Traces, metrics and logs, in one call shared with the worker (9.1). This also owns console
// logging — scope rendering included, so the per-request correlation id pushed by
// CorrelationMiddleware prefixes every line — because two hosts configuring that separately is
// exactly how the 4.3 IncludeScopes footgun happened. See docs/observability.md.
builder.AddArtificeWorksTelemetry(
    ArtificeWorksTelemetry.ApiServiceName,
    // Web-only instrumentation stays with the web host: the package carries a framework reference
    // to ASP.NET Core, and Infrastructure has no business acquiring one.
    configureTracing: tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.Configure<RedisConfiguration>(builder.Configuration.GetSection(nameof(RedisConfiguration)));

// RabbitMQ connection, event publisher, and per-request correlation context. Since 8.1 the
// publisher application code gets writes to the outbox rather than to the broker...
builder.Services.AddRabbitMqMessaging(builder.Configuration);

// ...and this is what drains it. The API runs a dispatcher of its own because the API is where
// the pipeline starts: a dropped `work-order.scheduled` strands an order before it has moved at
// all, which was the demo's most likely silent failure.
builder.Services.AddOutboxDispatcher();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Realtime (11.2). SignalR is the transport the DTO docstrings have named since Epic 4. The relay
// is a read-only, non-competing consumer of artifice.events on its own artifice.dashboard queue —
// it turns the board, the detail and the new event feed from polling into pushing, and it is where
// work-order.faulted/completed finally get a subscriber. It lives in the API because that is where
// browsers connect and the API already owns a broker connection (its OutboxDispatcher above).
builder.Services.AddSignalR();
builder.Services.AddSingleton<IDashboardBroadcaster, HubDashboardBroadcaster>();
builder.Services.AddHostedService<DashboardRelay>();

// Real probes (9.4), replacing the unconditional "Healthy" that has shipped since Epic 1 — which
// reported fine with Postgres down, and which M7 will point an orchestrator at.
builder.Services.AddArtificeWorksHealthChecks();

// RFC 7807 ProblemDetails everywhere: enables framework-generated errors (routing
// 404s, unhandled exceptions via UseExceptionHandler) to render as ProblemDetails.
// The customizer stamps a `code` on details built through IProblemDetailsService
// (the exception-handler path) so even unexpected 500s carry a reason code.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            context.ProblemDetails.Extensions["code"] = ProblemCodes.InternalError;
        }
    };
});

// Model-validation failures ([ApiController] auto-400) must carry the same
// contract as our hand-written errors: a ProblemDetails with a `code` extension.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var factory = context.HttpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, context.ModelState);
        problem.Extensions["code"] = ProblemCodes.ValidationFailed;
        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    };
});

var connectionString = builder.Configuration.GetConnectionString("ArtificeWorksDatabase")
    ?? throw new InvalidOperationException("Connection string 'ArtificeWorksDatabase' was not found.");

builder.Services.AddDbContext<ArtificeWorksDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
builder.Services.AddScoped<IMaterialReservationRepository, MaterialReservationRepository>();
builder.Services.AddScoped<IWorkOrderTimelineRepository, WorkOrderTimelineRepository>();

// Production + inspection (Epic 6). The API needs the inspection workflow because the manual
// verdict endpoint drives the same one the worker does — that is the point of the endpoint.
builder.Services.AddProductionAndInspection(builder.Configuration);

// Shipping (Epic 7). The API needs it for the same reason: the manual booking endpoint drives
// the same workflow the worker does, and releasing an order held at Delivery re-requests a
// booking (7.3), which means the API reads shipments too.
builder.Services.AddShipping(builder.Configuration);

// Recovery (8.3). The API is the half of this that a human touches: see what failed, and put it
// back. The worker owns the other half, the drain that turns parked messages into rows.
builder.Services.AddDeadLetters();

// The simulation's dials (10.2) and the pace policy the outbox dispatcher above consults (10.1).
// The API is where GET/PUT /system/simulation is served, and it is also a publisher, so it needs
// both halves.
builder.Services.AddSimulationSettings(builder.Configuration);

// The world sweep (10.4), for POST /system/world/reset. The same service the simulation host runs
// on a schedule — one code path, so the button and the schedule cannot drift apart.
builder.Services.AddWorldReset();

builder.Services.AddScoped<ProductHandler>();
builder.Services.AddScoped<WorkOrderHandler>();

var app = builder.Build();

// Seed the shared-platform catalog (components, product lines, BOMs) if the schema is ready.
// Idempotent and additive — it never restocks or overwrites a factory already in motion.
// Migrations stay a deliberate operator action (see Notes.md), so seeding is skipped rather
// than forced when the database isn't migrated yet.
await using (var scope = app.Services.CreateAsyncScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
    try
    {
        if ((await context.Database.GetPendingMigrationsAsync()).Any())
        {
            logger.LogWarning("Database has pending migrations; skipping catalog seed. Run 'dotnet ef database update'.");
        }
        else
        {
            await CatalogSeeder.SeedAsync(context);
        }
    }
    catch (Exception e)
    {
        logger.LogError(e, "Catalog seeding failed; the API will start with whatever catalog is already present.");
    }
}

// Turns unhandled exceptions into a ProblemDetails 500 (code `internal_error` is
// added by the handler below) instead of leaking a stack trace.
app.UseExceptionHandler();

// Establish the correlation id per request before any handler runs so published
// events and (in 4.3) log scopes carry it.
app.UseMiddleware<CorrelationMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();

// The dashboard's realtime endpoint (11.2). SignalR negotiates over HTTP then upgrades to a
// websocket; the dev proxy passes /hubs through with ws:true so the upgrade survives.
app.MapHub<DashboardHub>(DashboardHub.Route);

// Liveness and readiness are separate, and liveness checks NOTHING (9.4). The classic mistake is
// one endpoint checking dependencies: the database blips, every replica reports unhealthy, the
// orchestrator restarts all of them, and the restarts make it worse. A failing liveness probe
// means "restart me", and restarting the API does not fix a dead database.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthReport
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains(HealthChecks.ReadyTag),
    ResponseWriter = WriteHealthReport,
    ResultStatusCodes =
    {
        // Degraded is a 200 on purpose: an outbox backlog means the broker is unwell, and taking
        // this instance out of rotation would stop new work being recorded while doing nothing to
        // drain the queue. See OutboxLagHealthCheck.
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

// Kept as an alias for readiness so nothing already pointing at it breaks.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains(HealthChecks.ReadyTag),
    ResponseWriter = WriteHealthReport,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

// Per-check status and duration rather than the bare string — the thing an operator reads first.
static Task WriteHealthReport(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync(HealthReportJson.Serialize(report));
}
