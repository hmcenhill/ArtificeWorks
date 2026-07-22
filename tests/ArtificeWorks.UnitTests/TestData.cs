using System.Diagnostics.Metrics;

using ArtificeWorks.Application.Observability;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.UnitTests;

internal static class TestData
{
    public static Product DefaultProduct() => new Product("TEST-001", "Test Product");
    public static Product SomeOtherProduct() => new Product("TEST-002", "Different Product");

    /// <summary>
    /// A real <see cref="ArtificeWorksMetrics"/> over a real meter factory — not a stub. The
    /// instruments are no-ops when nothing is listening, so this costs nothing, and using the real
    /// thing means a unit test can attach a <c>MetricCollector</c> when it wants to assert on a
    /// count without the services being constructed any differently.
    /// </summary>
    public static ArtificeWorksMetrics Metrics(PipelineSnapshotCache? snapshot = null) => new(
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
        snapshot ?? new PipelineSnapshotCache());
}
