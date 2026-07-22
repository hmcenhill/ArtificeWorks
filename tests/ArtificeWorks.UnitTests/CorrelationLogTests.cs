using System.Diagnostics;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// 9.3's structural claim: <strong>the correlation id is a queryable field, not a piece of text
/// inside a message.</strong>
/// <para>
/// This is the exact failure mode the story names. Everything looks right on the console either
/// way — <c>CorrelationId:b1f3…</c> appears in the line — but a LogQL query for
/// <c>{correlation_id="b1f3…"}</c> only works if the scope arrives as structured state. A test
/// that asserted on the rendered message would pass in both worlds and prove nothing.
/// </para>
/// </summary>
public class CorrelationLogTests
{
    [Fact]
    public void A_line_logged_inside_the_correlation_scope_carries_the_id_as_a_field()
    {
        using var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug))
            .BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<CorrelationLogTests>>();
        var collector = provider.GetRequiredService<FakeLogCollector>();
        var correlationId = Guid.NewGuid();

        using (logger.BeginCorrelationScope(correlationId))
        {
            logger.LogInformation("Work order moved.");
        }

        var record = Assert.Single(collector.GetSnapshot());

        // The scope is a message-template scope, so its values arrive as key/value state — the
        // field name being the placeholder, which is why CorrelationLog defines it in one place.
        var scopeValues = record.Scopes
            .OfType<IReadOnlyList<KeyValuePair<string, object?>>>()
            .SelectMany(scope => scope)
            .ToList();

        var field = Assert.Single(scopeValues, pair => pair.Key == "CorrelationId");
        Assert.Equal(correlationId.ToString(), field.Value?.ToString());
    }

    /// <summary>
    /// The two ids have to agree on their name, or the pivot 9.3 is built around — correlation id
    /// to trace, trace back to lines — has a typo-shaped hole in it. The span attribute is the
    /// same string as the baggage key by construction; this pins that it stays that way.
    /// </summary>
    [Fact]
    public void The_span_attribute_and_the_baggage_key_are_the_same_name()
    {
        Assert.Equal(ArtificeWorksTelemetry.CorrelationIdAttribute, ArtificeWorksTelemetry.CorrelationBaggageKey);
        Assert.StartsWith("artificeworks.", ArtificeWorksTelemetry.CorrelationIdAttribute);
    }

    /// <summary>
    /// <see cref="ArtificeWorksTelemetry.StampWorkOrder"/> is called from workflow code that runs
    /// with and without an ambient activity. It must be a silent no-op in the second case rather
    /// than something a handler has to guard.
    /// </summary>
    [Fact]
    public void Stamping_a_work_order_is_a_no_op_with_no_ambient_activity()
    {
        Assert.Null(Activity.Current);

        // No listener, so no activity is ever created — the record of this call is that it did
        // not throw.
        ArtificeWorksTelemetry.StampWorkOrder(Guid.NewGuid());
    }
}
