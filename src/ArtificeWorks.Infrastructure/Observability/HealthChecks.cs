using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ArtificeWorks.Infrastructure.Observability;

/// <summary>
/// The dependency checks behind <c>/health/ready</c> (9.4).
/// <para>
/// <c>AddHealthChecks()</c> with nothing registered — which is what has shipped since Epic 1 — is
/// worse than no endpoint at all: it reports Healthy with Postgres down, and M7 will point a
/// container orchestrator at it.
/// </para>
/// </summary>
public static class HealthChecks
{
    /// <summary>Checks that must pass for the process to be considered able to serve.</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Registers the readiness checks. There is deliberately no <c>live</c> tag: liveness checks
    /// <em>nothing</em>, and the endpoint filters to an empty set (see <c>Program</c>).
    /// </summary>
    public static IHealthChecksBuilder AddArtificeWorksHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", tags: [ReadyTag])
            .AddCheck<PendingMigrationsHealthCheck>("migrations", tags: [ReadyTag])
            .AddCheck<BrokerHealthCheck>("rabbitmq", tags: [ReadyTag])
            .AddCheck<OutboxLagHealthCheck>("outbox", tags: [ReadyTag]);
}

/// <summary>
/// Postgres, reached through the <em>existing</em> <c>DbContext</c> rather than a new connection —
/// so the check exercises the same pool the application uses and cannot itself be the thing that
/// exhausts it.
/// </summary>
public sealed class DatabaseHealthCheck(ArtificeWorksDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context_, CancellationToken cancellationToken = default)
    {
        try
        {
            return await context.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Postgres is reachable.")
                : HealthCheckResult.Unhealthy("Postgres did not answer.");
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("Postgres is unreachable.", e);
        }
    }
}

/// <summary>
/// Pending migrations mean the schema the code expects is not the schema that is there — which in
/// this system already downgrades the API to a no-seed start. Serving traffic against it produces
/// column-not-found errors rather than a clear answer, so readiness says no.
/// </summary>
public sealed class PendingMigrationsHealthCheck(ArtificeWorksDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context_, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

            return pending.Count == 0
                ? HealthCheckResult.Healthy("Schema is up to date.")
                : HealthCheckResult.Unhealthy(
                    $"{pending.Count} pending migration(s): {string.Join(", ", pending)}. Run 'dotnet ef database update'.");
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("Could not read the migration history.", e);
        }
    }
}

/// <summary>
/// The broker, checked by asking the <em>shared</em> connection whether it is open.
/// <para>
/// <strong>It must not open a connection of its own.</strong> An aggressive readiness probe that
/// dials the broker once per call is a real way to exhaust one — and the connection object already
/// exists as a singleton, so the honest question is "is the one we have still up?".
/// </para>
/// <para>
/// <strong>Degraded, not Unhealthy — and that is 8.1's argument, not a softening of it.</strong>
/// A host with no broker can still do its most important job: record work and stage its events,
/// because the outbox turns a broker outage into a delay rather than a loss. Failing readiness
/// here would take the API out of rotation and stop new orders being accepted, while doing exactly
/// nothing to bring RabbitMQ back. The backlog this produces is visible in the outbox check right
/// next to it.
/// </para>
/// </summary>
public sealed class BrokerHealthCheck(IRabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // CreateChannelAsync forces the lazily-established connection to exist; a channel is
            // cheap and multiplexed over it, which is as close to "is it open" as the client gets
            // without reaching for internals.
            await using var channel = await connection.CreateChannelAsync(cancellationToken);
            return HealthCheckResult.Healthy("RabbitMQ connection is open.");
        }
        catch (Exception e)
        {
            return HealthCheckResult.Degraded(
                "RabbitMQ is unreachable. Events are accumulating in the outbox and will drain when it returns.", e);
        }
    }
}

/// <summary>
/// Outbox backlog, and it reports <strong>Degraded, never Unhealthy</strong>.
/// <para>
/// A backlog is a symptom of a downstream problem that removing this instance from rotation cannot
/// fix — and it would stop new work being <em>recorded</em> while doing nothing to drain what is
/// already queued. 8.1's whole design is that a backlog is safe: nothing is lost while it waits.
/// The number is worth surfacing loudly and worth refusing to act on.
/// </para>
/// <para>Reads the cached snapshot, so the probe costs no query no matter how often it is called.</para>
/// </summary>
public sealed class OutboxLagHealthCheck(PipelineSnapshotCache snapshot) : IHealthCheck
{
    /// <summary>Above this, the backlog is no longer "the dispatcher is mid-poll".</summary>
    public const double DegradedAfterSeconds = 30;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = snapshot.Current;

        var data = new Dictionary<string, object>
        {
            ["unsent"] = current.UnsentOutboxRows,
            ["lagSeconds"] = Math.Round(current.OutboxLagSeconds, 1),
            ["deadLettersUnreplayed"] = current.UnreplayedDeadLetters,
        };

        return Task.FromResult(current.OutboxLagSeconds > DegradedAfterSeconds
            ? HealthCheckResult.Degraded(
                $"Outbox is {current.OutboxLagSeconds:N0}s behind with {current.UnsentOutboxRows} unsent row(s). "
                + "Nothing is lost; the broker is probably unwell.", data: data)
            : HealthCheckResult.Healthy("Outbox is draining.", data));
    }
}
