using System.Text.Json;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ArtificeWorks.Infrastructure.Observability;

/// <summary>
/// The health response body (9.4): per-check status, duration and description, not the bare string
/// <c>Healthy</c>. This is the first thing an operator reads when something is wrong, and "which
/// dependency" is the only question they actually have.
/// <para>
/// Shared by both hosts so <c>/health/ready</c> means the same thing and looks the same whichever
/// half you point at.
/// </para>
/// </summary>
public static class HealthReportJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize(HealthReport report) => JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
            description = entry.Value.Description,
            // The exception message, not the stack: this body is read by a human at a terminal and
            // is served unauthenticated. The stack is in the logs, where it belongs.
            error = entry.Value.Exception?.Message,
            data = entry.Value.Data.Count == 0 ? null : entry.Value.Data
        })
    }, Options);
}
