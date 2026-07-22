namespace ArtificeWorks.Infrastructure.Observability;

/// <summary>
/// The <c>Telemetry</c> configuration section (9.1).
/// <para>
/// <strong>Telemetry is never load-bearing.</strong> An unreachable collector is a startup warning,
/// not a failure to boot, and never a blocked exporter stalling a handler. The pipeline's
/// reliability guarantees come from Epic 8 and must not acquire a dependency on the thing that
/// watches them — so every knob here has a default that works with nothing running.
/// </para>
/// </summary>
public sealed class TelemetryConfiguration
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Where to send OTLP. <strong>Empty means "export nothing"</strong>, which is the shipped
    /// default and what every test runs with: instruments and activities still exist (so
    /// <c>MetricCollector</c> and <c>ActivityListener</c> see them) but nothing goes on a socket.
    /// The compose stack's endpoint is set in <c>appsettings.Development.json</c>.
    /// </summary>
    public string OtlpEndpoint { get; set; } = string.Empty;

    /// <summary>gRPC (4317) or HttpProtobuf (4318). <c>grafana/otel-lgtm</c> listens on both.</summary>
    public string OtlpProtocol { get; set; } = "Grpc";

    /// <summary>Stamped on the resource as <c>service.version</c>.</summary>
    public string ServiceVersion { get; set; } = "0.9.0";

    /// <summary>
    /// Head sampling ratio. 1.0 — everything — is right for a demo factory doing single-figure
    /// orders a minute, and wrong for anything with real traffic; it is a knob so that is a config
    /// change rather than a code change.
    /// </summary>
    public double SamplingRatio { get; set; } = 1.0;

    /// <summary>
    /// Include SQL text on database spans. <strong>Development only by default</strong>: the
    /// statement is most of the value of a DB span and is also the thing you do not ship to a
    /// hosted backend without deciding to.
    /// </summary>
    public bool IncludeDbStatements { get; set; }

    /// <summary>How often the pipeline snapshot behind 9.2's gauges and <c>/system/stats</c> is refreshed.</summary>
    public int SnapshotIntervalMs { get; set; } = 5_000;

    public bool ExportsTelemetry => !string.IsNullOrWhiteSpace(OtlpEndpoint);
}
