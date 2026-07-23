using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Observability;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 10.2's dials and 10.3's origin, over the real API against real Postgres.
/// </summary>
public class SimulationApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public SimulationApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------ 10.2, the dials

    /// <summary>
    /// A fresh database seeds the row from configuration and reports where the values came from —
    /// which is the difference between "nobody has touched this" and "someone set it to exactly the
    /// default", and the first thing a dashboard control needs to know.
    /// </summary>
    [Fact]
    public async Task The_settings_endpoint_reports_the_configured_defaults()
    {
        var settings = await Get();

        Assert.NotNull(settings);
        Assert.False(settings!.PacingEnabled);
        Assert.False(settings.GenerationEnabled);
        Assert.True(settings.AutoInspect);
        Assert.Equal(0, settings.FailureRate);
    }

    /// <summary>
    /// A change reaches the live factory rather than only the row: the very next verdict source
    /// read must see it. Asserted through the API's own cache, because that is what the workflow
    /// services read.
    /// </summary>
    [Fact]
    public async Task A_put_changes_what_the_running_factory_does()
    {
        var before = await Get();

        var response = await Put(before! with { FailureRate = 0.4, AutoInspect = false });
        response.EnsureSuccessStatusCode();

        var applied = await response.Content.ReadFromJsonAsync<SimulationSettingsDto>();
        Assert.Equal(0.4, applied!.FailureRate);
        Assert.False(applied.AutoInspect);
        Assert.Equal("overridden", applied.Source);

        // The cache the workflow services actually read, in this process, now.
        var cache = _fixture.Services.GetRequiredService<SimulationSettingsCache>();
        Assert.Equal(0.4, cache.Current.FailureRate);
        Assert.False(cache.Current.AutoInspect);

        // And it survives a re-read: the row is the authority, not the cache.
        Assert.Equal(0.4, (await Get())!.FailureRate);

        await Put(before with { FailureRate = 0, AutoInspect = true });
    }

    /// <summary>
    /// An out-of-range value is refused with a stable code and <strong>changes nothing</strong> —
    /// the half of validation worth testing, since a rejected PUT that had already written half of
    /// itself would be worse than no validation at all.
    /// </summary>
    [Theory]
    [InlineData(1.5, 0, 20)]     // failure rate above 1.0
    [InlineData(0, -0.5, 20)]    // refusal rate below 0.0
    [InlineData(0, 0, 0)]        // a zero generation interval
    public async Task An_out_of_range_value_is_refused_and_the_live_value_is_unchanged(
        double failureRate, double refusalRate, int intervalSeconds)
    {
        var before = await Get();

        var response = await Put(before! with
        {
            FailureRate = failureRate,
            RefusalRate = refusalRate,
            GenerationIntervalSeconds = intervalSeconds,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("simulation_setting_out_of_range", await response.ReadProblemCodeAsync());

        var after = await Get();
        Assert.Equal(before.FailureRate, after!.FailureRate);
        Assert.Equal(before.RefusalRate, after.RefusalRate);
        Assert.Equal(before.GenerationIntervalSeconds, after.GenerationIntervalSeconds);
    }

    /// <summary>
    /// Pacing is quantized, so the endpoint reports the rung a duration resolved to. Without it,
    /// setting 5s and then 6s and seeing no change looks like a bug rather than the ladder working.
    /// </summary>
    [Fact]
    public async Task With_pacing_on_the_endpoint_reports_the_rung_each_duration_resolved_to()
    {
        var before = await Get();

        var response = await Put(before! with { PacingEnabled = true, PaceSecondsScheduled = 4.6 });
        var applied = await response.Content.ReadFromJsonAsync<SimulationSettingsDto>();

        Assert.NotNull(applied!.ResolvedRungs);
        Assert.Equal("5s", applied.ResolvedRungs!["work-order.scheduled"]);
        Assert.Equal("13s", applied.ResolvedRungs["work-order.materials-reserved"]);

        // And it says when the other hosts will have it, rather than implying "now".
        Assert.True(applied.TakesEffectWithinSeconds > 0);

        await Put(before);
    }

    /// <summary>
    /// A re-seed must never stomp a live override — <c>CatalogSeeder</c>'s contract, for the same
    /// reason. Boot the factory again with 0.9 dialled in and it is still 0.9.
    /// </summary>
    [Fact]
    public async Task Seeding_a_second_time_does_not_overwrite_an_override()
    {
        var before = await Get();
        await Put(before! with { FailureRate = 0.9 });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimulationSettingsRepository>();

        var reseeded = await repository.SeedIfMissing(SimulationSettings.ShippedDefaults);

        Assert.Equal(0.9, reseeded.FailureRate);

        await Put(before);
    }

    // ---------------------------------------------------------------- 10.3, the origin

    /// <summary>
    /// Every order carries a persisted origin, and a caller that says nothing is a visitor —
    /// which is what keeps <c>/system/stats</c> from silently reclassifying real demand.
    /// </summary>
    [Fact]
    public async Task An_order_created_without_an_origin_is_a_visitors()
    {
        var created = await CreateOrder(origin: null);

        Assert.Equal(WorkOrderOrigin.Visitor, created.Origin);
        Assert.Equal(WorkOrderOrigin.Visitor, await StoredOrigin(created.Id));
    }

    /// <summary>
    /// The generator's path: origin travels from the request body to the column, and back out on
    /// the DTO Epic 11 will filter a board with.
    /// </summary>
    [Fact]
    public async Task A_generated_order_is_marked_simulated_end_to_end()
    {
        var created = await CreateOrder(WorkOrderOrigin.Simulated);

        Assert.Equal(WorkOrderOrigin.Simulated, created.Origin);
        Assert.Equal(WorkOrderOrigin.Simulated, await StoredOrigin(created.Id));

        // And it comes back on a plain GET, not only on the create response.
        var fetched = await _fixture.Client.GetFromJsonAsync<WorkOrderDto>($"/work-orders/{created.Id}");
        Assert.Equal(WorkOrderOrigin.Simulated, fetched!.Origin);
    }

    /// <summary>
    /// <c>/system/stats</c> splits by origin, so a dashboard can draw real demand and simulated
    /// demand as separate lines rather than reporting robot traffic as throughput.
    /// </summary>
    [Fact]
    public async Task System_stats_separates_simulated_demand_from_real_demand()
    {
        await CreateOrder(WorkOrderOrigin.Simulated);
        await CreateOrder(WorkOrderOrigin.Visitor);

        // Take the reading rather than waiting for the timer.
        await _fixture.Services.GetRequiredService<PipelineSnapshotService>()
            .RefreshAsync(CancellationToken.None);

        var stats = await _fixture.Client.GetFromJsonAsync<SystemStatsDto>("/system/stats");

        Assert.NotNull(stats);
        Assert.True(stats!.WorkOrdersByOrigin["Simulated"] >= 1);
        Assert.True(stats.WorkOrdersByOrigin["Visitor"] >= 1);
        Assert.True(stats.WorkOrdersInFlightByOrigin["Simulated"] >= 1);
    }

    // -------------------------------------------------------------------------- helpers

    private async Task<SimulationSettingsDto?> Get() =>
        await _fixture.Client.GetFromJsonAsync<SimulationSettingsDto>("/system/simulation");

    private Task<HttpResponseMessage> Put(SimulationSettingsDto settings) =>
        _fixture.Client.PutAsJsonAsync("/system/simulation?updatedBy=test", settings);

    /// <summary>
    /// A product of this class's own, created through the API. The fixture migrates after the host
    /// is built, so <c>CatalogSeeder</c> has already declined to run — every test class here seeds
    /// what it needs.
    /// </summary>
    private const string ProductId = "SIM-ORIGIN-001";

    private async Task<WorkOrderDto> CreateOrder(WorkOrderOrigin? origin)
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "origin-test",
            ProductId = ProductId,
            ProductName = "Origin Automaton",
        });

        var request = new CreateWorkOrderRequest
        {
            Requestor = origin == WorkOrderOrigin.Simulated ? "sim:generator" : "a-visitor",
            ItemId = ProductId,
            Qty = 1,
        };

        if (origin is WorkOrderOrigin value)
        {
            request.Origin = value;
        }

        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<WorkOrderDto>())!;
    }

    private async Task<WorkOrderOrigin> StoredOrigin(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        return await context.WorkOrders
            .AsNoTracking()
            .Where(order => order.Id == workOrderId)
            .Select(order => order.Origin)
            .SingleAsync();
    }
}
