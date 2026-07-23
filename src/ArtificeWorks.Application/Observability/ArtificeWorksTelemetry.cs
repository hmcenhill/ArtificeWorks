using System.Diagnostics;

namespace ArtificeWorks.Application.Observability;

/// <summary>
/// The names. Every activity source, span attribute and baggage key this system emits is declared
/// here and nowhere else (9.1).
/// <para>
/// <strong>Why this is in Application rather than Infrastructure.</strong> The story put the
/// shared telemetry <em>registration</em> in Infrastructure, and that is where it lives
/// (<c>TelemetryServiceCollectionExtensions</c>). The <em>names</em> have to sit lower: the four
/// workflow services that tag spans are in Application, and Application cannot reference
/// Infrastructure. Domain would be lower still, but the domain deliberately depends on nothing —
/// including <c>System.Diagnostics</c> — so Application is the floor.
/// </para>
/// <para>
/// <strong>Attribute names are a contract.</strong> 9.3's log fields and 9.4's runbook queries
/// both key on these strings; renaming one silently breaks a Grafana query rather than a build,
/// which is exactly why they are constants in one file.
/// </para>
/// </summary>
public static class ArtificeWorksTelemetry
{
    /// <summary>The one <see cref="System.Diagnostics.ActivitySource"/> this system's own code emits from.</summary>
    public const string ActivitySourceName = "ArtificeWorks.Pipeline";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // ---------------------------------------------------------------- domain span attributes

    /// <summary>
    /// The human-facing id from 4.3, stamped on spans as well as log lines so a correlation id
    /// and a trace can be pivoted between in either direction. Deliberately the same string as
    /// the baggage key and as <c>CorrelationLog</c>'s structured field.
    /// </summary>
    public const string CorrelationIdAttribute = "artificeworks.correlation_id";

    /// <summary>The thing a visitor actually knows — an order — which is what makes a trace searchable.</summary>
    public const string WorkOrderIdAttribute = "artificeworks.work_order_id";

    public const string EventTypeAttribute = "artificeworks.event_type";

    /// <summary>Which delivery this is, 1-based. What distinguishes retried consumer spans in one trace.</summary>
    public const string AttemptAttribute = "artificeworks.attempt";

    public const string OutcomeAttribute = "artificeworks.outcome";

    /// <summary>
    /// How long a paced message will rest in a delay queue before delivery (10.1). It explains the
    /// seconds-wide gap between a producer span and its consumer span: the factory is working, not
    /// stalled.
    /// </summary>
    public const string PacedMsAttribute = "artificeworks.paced_ms";

    /// <summary>
    /// Who a work order came from — a visitor or the simulation (10.3). Two values, so it is safe
    /// as a metric dimension as well as a span attribute; see <c>ArtificeWorksMetrics</c>.
    /// </summary>
    public const string OriginAttribute = "artificeworks.origin";

    /// <summary>
    /// The correlation id's key in OTel baggage — the decision taken at grooming. <c>traceparent</c>
    /// carries causality; this carries the id a human can read out loud. Baggage propagates
    /// automatically over HTTP and is injected into AMQP headers by the raw publisher.
    /// </summary>
    public const string CorrelationBaggageKey = CorrelationIdAttribute;

    // -------------------------------------------------- OTel messaging semantic conventions

    public const string MessagingSystem = "messaging.system";
    public const string MessagingOperation = "messaging.operation";
    public const string MessagingDestination = "messaging.destination.name";
    public const string MessagingMessageId = "messaging.message.id";

    /// <summary>The value of <c>messaging.system</c> for every span this system emits.</summary>
    public const string RabbitMq = "rabbitmq";

    /// <summary>
    /// Tags the ambient span with the order it is about. Called by the workflow services once they
    /// know which order they are working on — which is what makes a trace findable by the only
    /// identifier a visitor actually has. A no-op when nothing is listening.
    /// </summary>
    public static void StampWorkOrder(Guid workOrderId) =>
        Activity.Current?.SetTag(WorkOrderIdAttribute, workOrderId.ToString());

    /// <summary>Tags the ambient span with who the order came from (10.3) — a visitor or the simulation.</summary>
    public static void StampOrigin(string origin) =>
        Activity.Current?.SetTag(OriginAttribute, origin);

    /// <summary>Names the service in the OTel resource. Both hosts pass one of these, and nothing else.</summary>
    public const string ApiServiceName = "artificeworks.api";
    public const string WorkerServiceName = "artificeworks.workers";

    /// <summary>The third host (10.1). It publishes and schedules; it consumes nothing.</summary>
    public const string SimulationServiceName = "artificeworks.simulation";
}
