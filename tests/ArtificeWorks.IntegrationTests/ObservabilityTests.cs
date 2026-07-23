using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 9.4's probes and 9.2's snapshot endpoint, over the real API.
/// <para>
/// The claim being tested is that <c>/health/live</c> and <c>/health/ready</c> <em>mean different
/// things</em> — the endpoint that has returned an unconditional "Healthy" since Epic 1 could pass
/// a smoke test while Postgres was on fire.
/// </para>
/// </summary>
public class ObservabilityTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ObservabilityTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Liveness_reports_healthy_and_checks_nothing()
    {
        var response = await _fixture.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());

        // No checks ran. That is the point: a failing liveness probe means "restart me", and
        // restarting the API does not fix a dead database.
        Assert.Empty(body.RootElement.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Readiness_names_every_dependency_it_checked()
    {
        var response = await _fixture.Client.GetAsync("/health/ready");

        // 200 even though this fixture has no RabbitMQ: an unreachable broker is Degraded, not
        // Unhealthy, because the outbox means the API can still record work. That is 8.1's design
        // showing up as a health-check decision.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var checks = body.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("name").GetString()!,
                check => check.GetProperty("status").GetString());

        // The shape the runbook promises: per-check status and duration, not a bare string.
        Assert.Contains("postgres", checks.Keys);
        Assert.Contains("migrations", checks.Keys);
        Assert.Contains("rabbitmq", checks.Keys);
        Assert.Contains("outbox", checks.Keys);

        // The database is real here and must say so — a probe that reported Healthy for a
        // dependency it never touched is exactly what this epic replaced.
        Assert.Equal("Healthy", checks["postgres"]);

        foreach (var check in body.RootElement.GetProperty("checks").EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(check.GetProperty("status").GetString()));
            Assert.True(check.GetProperty("durationMs").GetDouble() >= 0);
        }
    }

    /// <summary>/health kept working, because 9.4 promised it would.</summary>
    [Fact]
    public async Task The_old_health_endpoint_still_answers()
    {
        var response = await _fixture.Client.GetAsync("/health");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task System_stats_reflects_the_database_without_a_metrics_backend()
    {
        // Arrange — a work order the snapshot can count.
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "stats tester",
            ProductId = "PRD-STATS",
            ProductName = "Statistical Automaton"
        });

        var created = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "stats tester",
            ItemId = "PRD-STATS",
            Qty = 1
        });
        Assert.True(created.IsSuccessStatusCode);

        // Take the reading deterministically rather than waiting for the timer.
        await Snapshot().RefreshAsync(CancellationToken.None);

        var stats = await _fixture.Client.GetFromJsonAsync<SystemStatsDto>("/system/stats");

        Assert.NotNull(stats);
        Assert.True(stats!.Fresh);
        Assert.True(stats.WorkOrdersTotal >= 1);
        Assert.True(stats.WorkOrdersInFlight >= 1);
        Assert.Contains("Intake", stats.WorkOrdersByStatus.Keys);

        // The outbox row for WorkOrderCreated is there and unsent — the fixture removes the
        // dispatcher — which is a real, checkable number rather than a placeholder.
        Assert.True(stats.OutboxUnsent >= 1);
    }

    /// <summary>
    /// The backlog rule, tested directly because arranging a 30-second-old outbox row through the
    /// API would mean either waiting or lying to the clock. <strong>Degraded, never Unhealthy</strong>:
    /// removing this instance from rotation cannot drain a queue, and would stop new work being
    /// recorded while it didn't.
    /// </summary>
    [Fact]
    public async Task Outbox_backlog_degrades_readiness_rather_than_failing_it()
    {
        var cache = new PipelineSnapshotCache();
        cache.Update(new PipelineSnapshot(
            CapturedUtc: DateTime.UtcNow,
            WorkOrdersByStatus: new Dictionary<string, long>(),
            UnsentOutboxRows: 500,
            OutboxLagSeconds: OutboxLagHealthCheck.DegradedAfterSeconds + 60,
            UnreplayedDeadLetters: 0,
            TotalWorkOrders: 0,
            WorkOrdersByOrigin: new Dictionary<string, long>(),
            InFlightByOrigin: new Dictionary<string, long>(),
            StockLevelRatio: 1));

        var result = await new OutboxLagHealthCheck(cache)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Nothing is lost", result.Description);
    }

    /// <summary>
    /// Readiness has to actually fail when Postgres does — the whole reason the epic touched
    /// <c>/health</c>. Pointed at a port nothing is listening on rather than by stopping the
    /// shared container, so the rest of the suite is unaffected.
    /// </summary>
    [Fact]
    public async Task Readiness_fails_when_the_database_is_unreachable()
    {
        var options = new DbContextOptionsBuilder<ArtificeWorksDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nowhere;Username=none;Password=none;Timeout=1")
            .Options;

        await using var context = new ArtificeWorksDbContext(options);

        var result = await new DatabaseHealthCheck(context)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // Resolved as a scheduled task rather than a hosted service since 10.1 folded the standalone
    // timer loops onto PeriodicTaskHost. Still the same class, still refreshed on demand here so a
    // test asserts on a reading it took rather than on one it waited for.
    private PipelineSnapshotService Snapshot() =>
        _fixture.Services.GetRequiredService<PipelineSnapshotService>();
}
