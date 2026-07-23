namespace ArtificeWorks.Infrastructure.Scheduling;

/// <summary>
/// A job that runs on an interval, forever, in whichever host composes it (10.1).
/// <para>
/// <strong>This is 9.2's "fold the timers" note, cashed.</strong> By the end of Epic 9 there were
/// three hand-rolled <c>while (!stopping) { work(); await Task.Delay(n); }</c> loops, each with its
/// own copy of the two rules that matter — never let an exception kill the loop, and never let a
/// slow pass turn into an unbounded backlog. Epic 10 wanted three more.
/// </para>
/// <para>
/// <strong>What the fold is, and what it is not.</strong> It is one <em>type</em> each host reuses,
/// not one process that owns every timer. <c>PipelineSnapshotService</c> cannot move into the
/// simulation host: <c>GET /system/stats</c> reads the snapshot in the API and the worker's gauges
/// read it in the worker, so all three keep refreshing their own. Say so out loud, because
/// "we folded the timers" and "there are still timers in three processes" look contradictory
/// otherwise.
/// </para>
/// <para>
/// <strong>What deliberately does not implement this.</strong> <c>OutboxDispatcher</c> and
/// <c>ParkedQueueDrain</c> stay as they are. They are consumers with their own lifecycles — one
/// drains until empty rather than once per tick, the other holds an AMQP channel open — and
/// forcing them through a scheduler would be the abstraction earning its keep in the wrong place.
/// </para>
/// </summary>
public interface IScheduledTask
{
    /// <summary>Used in log lines and in the task's span name. Stable, short, kebab-case.</summary>
    string Name { get; }

    /// <summary>
    /// How long to wait between the end of one run and the start of the next. Read fresh before
    /// every wait, so a task whose interval lives on 10.2's settings row can be retuned at
    /// runtime without a restart.
    /// </summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Whether to run once immediately at startup rather than waiting out the first interval.
    /// True for anything whose output something else reads (a cache refresh); false for anything
    /// that changes the world (a sweep), so a restart loop cannot become a work loop.
    /// </summary>
    bool RunOnStartup => true;

    Task RunAsync(CancellationToken cancellationToken);
}
