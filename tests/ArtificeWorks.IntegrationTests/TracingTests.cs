using System.Diagnostics;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The load-bearing claim of 9.1: <strong>trace context survives the outbox</strong>.
/// <para>
/// 8.1 moved every publish from after the commit to staged inside it, so the event reaches the
/// wire up to a poll interval later, on a background thread, with no ambient activity. A default
/// OpenTelemetry setup produces something worse than no tracing there: every trace ends neatly at
/// the commit, and the dispatcher starts a fresh parentless one-span trace for each publish. Fully
/// instrumented, entirely disconnected, and it looks correct until you follow an order across a
/// stage boundary — which is exactly what these tests do instead of eyeballing a waterfall.
/// </para>
/// <para>Requires Docker (Testcontainers Postgres). No broker: the claim is about the row.</para>
/// </summary>
public class TracingTests : IClassFixture<OutboxFixture>
{
    private readonly OutboxFixture _fixture;

    public TracingTests(OutboxFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The whole story in one test: stage inside an activity, let the activity <em>end</em>, then
    /// dispatch — and the publish still happens under the original trace.
    /// </summary>
    [Fact]
    public async Task An_outbox_row_publishes_under_the_trace_it_was_staged_in()
    {
        using var listener = ListenToPipeline();

        string expectedTraceId;
        string stagingSpanId;

        // The request. It opens an activity, stages the event, commits — and then the activity
        // ends, exactly as an HTTP request's does.
        using (var activity = ArtificeWorksTelemetry.ActivitySource.StartActivity("test request"))
        {
            Assert.NotNull(activity);
            expectedTraceId = activity!.TraceId.ToString();
            stagingSpanId = activity.SpanId.ToString();

            await using var scope = _fixture.Services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

            await publisher.PublishAsync(new WorkOrderScheduled(
                Guid.NewGuid(), "PRD-TRACE", "Traced Automaton", 1, DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        // No ambient activity at all now — the dispatcher's real situation.
        Assert.Null(Activity.Current);

        var dispatched = await _fixture.NewDispatcher().DispatchBatchAsync(CancellationToken.None);
        Assert.True(dispatched > 0);

        var published = _fixture.Broker.Published.Last();
        Assert.NotNull(published.ParentContext);

        // Same trace, and specifically parented to the span that staged the row — not merely
        // "some span in the same trace", which is what a sloppier capture would produce.
        Assert.Equal(expectedTraceId, published.ParentContext!.Value.TraceId.ToString());
        Assert.Equal(stagingSpanId, published.ParentContext.Value.SpanId.ToString());

        // And it is marked remote: this thread is not the one that made it, and the gap between
        // them is a database row.
        Assert.True(published.ParentContext.Value.IsRemote);
    }

    /// <summary>
    /// The other half of the rule: <strong>untraced, never broken</strong>. A row staged with no
    /// ambient activity — a background service, a test, a replayed dead letter — must publish
    /// cleanly rather than throwing or inventing a parent.
    /// </summary>
    [Fact]
    public async Task A_row_staged_outside_any_activity_still_publishes()
    {
        // Deliberately no listener and no activity: Activity.Current is null.
        Assert.Null(Activity.Current);

        var eventId = Guid.NewGuid();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

            await publisher.PublishAsync(new WorkOrderScheduled(
                eventId, "PRD-UNTRACED", "Untraced Automaton", 1, DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.NewContext())
        {
            var row = await context.OutboxMessages
                .AsNoTracking()
                .OrderByDescending(message => message.Id)
                .FirstAsync(message => message.Payload.Contains("PRD-UNTRACED"));

            Assert.Null(row.TraceParent);
        }

        var dispatched = await _fixture.NewDispatcher().DispatchBatchAsync(CancellationToken.None);
        Assert.True(dispatched > 0);

        var published = _fixture.Broker.Published.Single(message => message.Payload.Contains("PRD-UNTRACED"));
        Assert.Null(published.ParentContext);
    }

    /// <summary>
    /// An <see cref="ActivitySource"/> only creates activities when something is listening — which
    /// is exactly why telemetry costs nothing when it is off, and why a test that wants real spans
    /// has to say so.
    /// </summary>
    private static ActivityListener ListenToPipeline()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ArtificeWorksTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
