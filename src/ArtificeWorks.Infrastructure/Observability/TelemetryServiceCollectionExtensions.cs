using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Scheduling;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ArtificeWorks.Infrastructure.Observability;

/// <summary>
/// The one place either host configures telemetry (9.1). Resource, exporter, sampler, console
/// logging and the shared instruments all live here — two hosts configuring this independently is
/// how they drift, and a trace that spans them only renders if both agree on the propagator and
/// both name their service the same way every time.
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Wires traces, metrics and logs for a host.
    /// </summary>
    /// <param name="serviceName">
    /// <see cref="ArtificeWorksTelemetry.ApiServiceName"/> or
    /// <see cref="ArtificeWorksTelemetry.WorkerServiceName"/>. It becomes <c>service.name</c>, which
    /// is what Grafana groups a trace's spans by, so it is a constant rather than a string.
    /// </param>
    /// <param name="configureTracing">
    /// Host-specific instrumentation. The API passes <c>AddAspNetCoreInstrumentation</c> — that
    /// package carries a framework reference to ASP.NET Core, and Infrastructure has no business
    /// acquiring one to serve a caller that may not be a web host.
    /// </param>
    public static IHostApplicationBuilder AddArtificeWorksTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var section = builder.Configuration.GetSection(TelemetryConfiguration.SectionName);
        builder.Services.Configure<TelemetryConfiguration>(section);

        var config = section.Get<TelemetryConfiguration>() ?? new TelemetryConfiguration();

        // The instruments and the snapshot they read. Registered here rather than in each host so
        // "telemetry is on" is one call, and so a host cannot end up with a metrics recorder but no
        // snapshot behind its gauges.
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<PipelineSnapshotCache>();
        builder.Services.AddSingleton<ArtificeWorksMetrics>();
        builder.Services.AddScheduledTask<PipelineSnapshotService>();

        ConfigureLogging(builder, serviceName, config);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: config.ServiceVersion, serviceInstanceId: Environment.MachineName)
                .AddAttributes([
                    new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)
                ]))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(config.SamplingRatio >= 1.0
                        ? new AlwaysOnSampler()
                        : new ParentBasedSampler(new TraceIdRatioBasedSampler(config.SamplingRatio)))
                    // Our own spans: the AMQP producer/consumer pair and the outbox publish that
                    // stitches them to the request that caused them.
                    .AddSource(ArtificeWorksTelemetry.ActivitySourceName)
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation(options =>
                    {
                        // The statement text is most of what a DB span is worth — and is also the
                        // thing you don't ship to a hosted backend without deciding to. Off unless
                        // configuration says otherwise; Development turns it on.
                        //
                        // The instrumentation's `SetDbStatementForText` flag is gone as of 1.12:
                        // the statement now follows the stable database semantic conventions and
                        // is always emitted. Stripping the tag in an enricher is the remaining way
                        // to say no, and setting a tag to null is how Activity removes one.
                        if (!config.IncludeDbStatements)
                        {
                            options.EnrichWithIDbCommand = (activity, _) =>
                            {
                                activity.SetTag("db.query.text", null);
                                activity.SetTag("db.statement", null);
                            };
                        }
                    });

                configureTracing?.Invoke(tracing);

                if (config.ExportsTelemetry)
                {
                    tracing.AddOtlpExporter(otlp => Configure(otlp, config));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ArtificeWorksMetrics.MeterName)
                    // Free, and half of what a real dashboard shows: request rates, connection
                    // counts, EF command durations, GC and threadpool pressure.
                    .AddMeter("Microsoft.AspNetCore.Hosting")
                    .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                    .AddMeter("Microsoft.EntityFrameworkCore")
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                configureMetrics?.Invoke(metrics);

                if (config.ExportsTelemetry)
                {
                    metrics.AddOtlpExporter((otlp, reader) =>
                    {
                        Configure(otlp, config);
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000;
                    });
                }
            });

        return builder;
    }

    /// <summary>
    /// Console <em>and</em> OTLP, never either/or (9.3). OTLP is what the runbook queries; the
    /// console is what a person watches during a demo, what <c>docker logs</c> shows, and the only
    /// thing left when the observability stack itself is what failed.
    /// <para>
    /// <strong>This is also where the <c>IncludeScopes</c> footgun from 4.3 is settled.</strong>
    /// Scope rendering was configured in code in both hosts while the <c>Logging:Console</c>
    /// appsettings path claimed to own it — two mechanisms, one of which silently lost. It is
    /// configured here, in code, in one place for both hosts, because the correlation scope is a
    /// correctness property of the logs rather than a preference: an operator who turns scopes off
    /// in configuration would remove the correlation id from every console line and get no warning.
    /// </para>
    /// </summary>
    private static void ConfigureLogging(IHostApplicationBuilder builder, string serviceName, TelemetryConfiguration config)
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = false;
            });
        }
        else
        {
            // Elsewhere the console is being scraped, not read, so give it structure.
            builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        }

        builder.Logging.AddOpenTelemetry(logging =>
        {
            // Scope values must arrive as structured attributes rather than being flattened into
            // the message — otherwise CorrelationId is only inside the rendered text, and a LogQL
            // query for it cannot work. That is the failure mode 9.3's tests check for.
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            logging.ParseStateValues = true;

            logging.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName, serviceVersion: config.ServiceVersion));

            if (config.ExportsTelemetry)
            {
                logging.AddOtlpExporter(otlp => Configure(otlp, config));
            }
        });
    }

    private static void Configure(OtlpExporterOptions options, TelemetryConfiguration config)
    {
        options.Endpoint = new Uri(config.OtlpEndpoint);
        options.Protocol = string.Equals(config.OtlpProtocol, "HttpProtobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        // A collector that isn't there must never become a stalled handler. The exporter retries in
        // the background and drops what it cannot send; this timeout is the ceiling on how long a
        // flush can hold anything up.
        options.TimeoutMilliseconds = 5_000;
    }
}
