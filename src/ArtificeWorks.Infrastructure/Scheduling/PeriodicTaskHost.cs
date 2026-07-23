using System.Diagnostics;

using ArtificeWorks.Application.Observability;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Infrastructure.Scheduling;

/// <summary>
/// Runs every registered <see cref="IScheduledTask"/>, each on its own interval, in one hosted
/// service (10.1).
/// <para>
/// <strong>One loop per task, not one loop over all tasks.</strong> A shared tick would make the
/// slowest task the pacer for every other one — a six-hourly world sweep and a five-second cache
/// refresh have nothing to say to each other — and a task that hung would silently stop the rest.
/// </para>
/// <para>
/// <strong>A task never kills its loop, and never kills the host.</strong> A throwing pass is
/// logged and the loop waits out its interval, exactly as <c>OutboxDispatcher</c> and
/// <c>PipelineSnapshotService</c> each learned to do independently. This is the rule those three
/// copies existed to state, held in one place.
/// </para>
/// <para>
/// <strong>Each pass gets a span.</strong> Named <c>task {Name}</c>, so a slow sweep shows up in
/// Tempo as a slow thing rather than as a gap — and so a task that publishes or writes has a
/// parent for the outbox row it stages (9.1's <c>traceparent</c> capture).
/// </para>
/// </summary>
public sealed class PeriodicTaskHost : BackgroundService
{
    private readonly IEnumerable<IScheduledTask> _tasks;
    private readonly ILogger<PeriodicTaskHost> _logger;

    public PeriodicTaskHost(IEnumerable<IScheduledTask> tasks, ILogger<PeriodicTaskHost> logger)
    {
        _tasks = tasks;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = _tasks.ToList();

        if (tasks.Count == 0)
        {
            _logger.LogDebug("No scheduled tasks registered; the periodic task host has nothing to run.");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Periodic task host started with {TaskCount} task(s): {Tasks}.",
            tasks.Count, string.Join(", ", tasks.Select(task => $"{task.Name} every {task.Interval}")));

        return Task.WhenAll(tasks.Select(task => RunLoopAsync(task, stoppingToken)));
    }

    private async Task RunLoopAsync(IScheduledTask task, CancellationToken stoppingToken)
    {
        if (!task.RunOnStartup)
        {
            if (!await WaitAsync(task, stoppingToken))
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(task, stoppingToken);

            if (!await WaitAsync(task, stoppingToken))
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(IScheduledTask task, CancellationToken stoppingToken)
    {
        using var activity = ArtificeWorksTelemetry.ActivitySource.StartActivity(
            $"task {task.Name}", ActivityKind.Internal);

        var started = Stopwatch.GetTimestamp();

        try
        {
            await task.RunAsync(stoppingToken);

            _logger.LogDebug(
                "Scheduled task {Task} completed in {ElapsedMs}ms.",
                task.Name, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception e)
        {
            // The loop must never die. A task that stops running is silence, which is the failure
            // mode Epic 8 spent itself removing and Epic 9 spent itself making visible.
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            _logger.LogError(e, "Scheduled task {Task} failed; it will run again after its interval.", task.Name);
        }
    }

    /// <summary>
    /// Waits out the task's interval. The interval is read <em>now</em> rather than captured at
    /// startup, so a task backed by 10.2's settings row picks up a retune on the next wait.
    /// Returns false when the host is shutting down.
    /// </summary>
    private static async Task<bool> WaitAsync(IScheduledTask task, CancellationToken stoppingToken)
    {
        var interval = task.Interval;
        if (interval <= TimeSpan.Zero)
        {
            // A misconfigured zero interval would spin the CPU. One second is slow enough to be
            // harmless and fast enough that the mistake is obvious.
            interval = TimeSpan.FromSeconds(1);
        }

        try
        {
            await Task.Delay(interval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scheduled task and, once, the host that runs them. Calling this is the whole
    /// of composing a periodic job — there is no second registration to forget.
    /// </summary>
    public static IServiceCollection AddScheduledTask<TTask>(this IServiceCollection services)
        where TTask : class, IScheduledTask
    {
        services.AddSingleton<TTask>();
        services.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<TTask>());
        services.AddPeriodicTaskHost();
        return services;
    }

    /// <summary>Registers the host itself. Idempotent — several <c>AddScheduledTask</c> calls yield one host.</summary>
    public static IServiceCollection AddPeriodicTaskHost(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ImplementationType == typeof(PeriodicTaskHost)))
        {
            return services;
        }

        services.AddHostedService<PeriodicTaskHost>();
        return services;
    }
}
