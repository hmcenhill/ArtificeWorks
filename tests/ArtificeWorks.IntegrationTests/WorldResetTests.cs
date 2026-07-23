using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Messaging.DeadLetters;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// 10.4's world sweep, against real Postgres.
/// <para>
/// The claims here are all database claims — restock is idempotent, the retire cascade leaves no
/// orphans, and an in-flight order is <em>provably</em> untouched — so they are raced against the
/// real schema rather than a stub. The cascade in particular is the schema's work, not the
/// repository's, and a test against an in-memory provider would prove nothing about it.
/// </para>
/// </summary>
public class WorldResetTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public WorldResetTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Restock is a conditional set to the seed level, so two runs leave the same answer. A blind
    /// increment that ran twice would be a factory that mysteriously has more brass panels than it
    /// started with.
    /// </summary>
    [Fact]
    public async Task A_sweep_restores_stock_to_seed_levels_and_is_idempotent()
    {
        var componentId = await DrawDownSomeStock(by: 40);

        var first = await Sweep();
        Assert.True(first.ComponentsRestocked >= 1);
        Assert.Equal(await SeedLevel(componentId), await OnHand(componentId));

        // Second run: nothing left to do, and nothing gained.
        var second = await Sweep();
        Assert.Equal(0, second.ComponentsRestocked);
        Assert.Equal(await SeedLevel(componentId), await OnHand(componentId));
    }

    /// <summary>
    /// The scope rule, in one test: terminal and stuck orders past the cutoff go, and an order
    /// mid-pipeline stays — no matter how old. A live order is never reset out from under a visitor.
    /// </summary>
    [Fact]
    public async Task A_sweep_retires_old_finished_and_stuck_orders_and_leaves_an_in_flight_one_alone()
    {
        var completed = await AgedOrder(WorkOrderStatus.Completed, hoursOld: 48);
        var cancelled = await AgedOrder(WorkOrderStatus.Cancelled, hoursOld: 48);
        var held = await AgedOrder(WorkOrderStatus.OnHold, hoursOld: 48);
        var faulted = await AgedOrder(WorkOrderStatus.Fault, hoursOld: 48);

        // Mid-pipeline and just as old — the case the rule exists for.
        var inFlight = await AgedOrder(WorkOrderStatus.InProcess, hoursOld: 48);

        // Finished, but recent: inside the retention window, so it stays too.
        var recent = await AgedOrder(WorkOrderStatus.Completed, hoursOld: 1);

        var result = await Sweep();

        Assert.True(result.OrdersRetired >= 4);

        Assert.False(await Exists(completed));
        Assert.False(await Exists(cancelled));
        Assert.False(await Exists(held));
        Assert.False(await Exists(faulted));

        Assert.True(await Exists(inFlight));
        Assert.True(await Exists(recent));
    }

    /// <summary>
    /// The cascade is what makes retiring an order safe rather than a source of orphan rows. State
    /// history hangs off the order with <c>ON DELETE CASCADE</c>; if that ever stops being true,
    /// this fails rather than the database quietly filling with rows nothing points at.
    /// </summary>
    [Fact]
    public async Task Retiring_an_order_leaves_nothing_behind_it()
    {
        var retired = await AgedOrder(WorkOrderStatus.Completed, hoursOld: 48);

        await using (var arrange = _fixture.Services.CreateAsyncScope())
        {
            var context = arrange.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            Assert.True(await context.OrderStateHistory.AnyAsync(entry => entry.WorkOrderId == retired));
        }

        await Sweep();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var after = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        Assert.False(await after.OrderStateHistory.AnyAsync(entry => entry.WorkOrderId == retired));
        Assert.False(await after.MaterialReservations.AnyAsync(row => row.WorkOrderId == retired));
        Assert.False(await after.Shipments.AnyAsync(row => row.WorkOrderId == retired));
        Assert.False(await after.ProductionRuns.AnyAsync(row => row.WorkOrderId == retired));
        Assert.False(await after.InspectionRuns.AnyAsync(row => row.WorkOrderId == retired));
    }

    /// <summary>
    /// The two things the sweep must never touch: the catalog it would have to re-seed, and the
    /// dead letters Epic 12 exists to display. This is the difference between a sweep and a
    /// truncate, and it is the whole reason the grooming chose the former.
    /// </summary>
    [Fact]
    public async Task A_sweep_never_touches_the_catalog_or_the_dead_letters()
    {
        await using (var arrange = _fixture.Services.CreateAsyncScope())
        {
            var context = arrange.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
            context.DeadLetters.Add(new DeadLetter(
                "work-order.scheduled", "{}", Guid.NewGuid(), Guid.NewGuid(), 4,
                "a failure nobody has looked at yet", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        var (productsBefore, componentsBefore, lettersBefore) = await CatalogCounts();

        await Sweep();

        var (productsAfter, componentsAfter, lettersAfter) = await CatalogCounts();

        Assert.Equal(productsBefore, productsAfter);
        Assert.Equal(componentsBefore, componentsAfter);
        Assert.Equal(lettersBefore, lettersAfter);
    }

    /// <summary>
    /// The endpoint and the schedule run the same method — the story's first acceptance criterion,
    /// and the reason there is no second code path to keep in step.
    /// </summary>
    [Fact]
    public async Task The_reset_endpoint_runs_the_same_sweep_and_reports_what_it_did()
    {
        await AgedOrder(WorkOrderStatus.Completed, hoursOld: 48);
        await DrawDownSomeStock(by: 25);

        var response = await _fixture.Client.PostAsync("/system/world/reset?triggeredBy=test", null);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<WorldResetResult>();

        Assert.NotNull(result);
        Assert.True(result!.OrdersRetired >= 1);
        Assert.True(result.ComponentsRestocked >= 1);
        Assert.Contains("Restocked", result.Summary);
    }

    // -------------------------------------------------------------------------- helpers

    private async Task<WorldResetResult> Sweep()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorldResetService>().Sweep("test");
    }

    /// <summary>
    /// A component of this class's own, drawn down the way picking draws one down — so the sweep
    /// has something to put back. The fixture migrates after the host is built, so
    /// <c>CatalogSeeder</c> has already declined to run and there is no catalog to borrow.
    /// </summary>
    private const string ComponentId = "CMP-SWEEP-TEST";

    private async Task<string> DrawDownSomeStock(uint by)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        var component = await context.Components
            .SingleOrDefaultAsync(c => c.ComponentId == ComponentId);

        if (component is null)
        {
            // The constructor sets seed_on_hand from on_hand: "what it starts with" is decided once.
            component = new Domain.Models.Materials.Component(ComponentId, "Sweep Test Widget", 500);
            context.Components.Add(component);
        }

        component.TryConsume(by);
        await context.SaveChangesAsync();

        return ComponentId;
    }

    /// <summary>
    /// A real order, created through the API and then aged and moved by hand. The status and the
    /// timestamp are what the sweep reads, and there is no endpoint that produces a 48-hour-old
    /// Completed order on demand.
    /// </summary>
    private async Task<Guid> AgedOrder(WorkOrderStatus status, int hoursOld)
    {
        await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "sweep-test",
            ProductId = "SWEEP-TEST-001",
            ProductName = "Sweep Test Automaton",
        });

        var response = await _fixture.Client.PostAsJsonAsync("/work-orders", new CreateWorkOrderRequest
        {
            Requestor = "sweep-test",
            ItemId = "SWEEP-TEST-001",
            Qty = 1,
        });
        response.EnsureSuccessStatusCode();

        var created = (await response.Content.ReadFromJsonAsync<WorkOrderDto>())!;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        await context.Database.ExecuteSqlAsync($"""
            UPDATE work_orders
            SET "CurrentStatus" = {status.ToString()}, "UpdatedUtc" = {DateTime.UtcNow.AddHours(-hoursOld)}
            WHERE "Id" = {created.Id}
            """);

        return created.Id;
    }

    private async Task<bool> Exists(Guid workOrderId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();
        return await context.WorkOrders.AnyAsync(order => order.Id == workOrderId);
    }

    private async Task<uint> OnHand(string componentId) => await Component(componentId, c => c.OnHand);

    private async Task<uint> SeedLevel(string componentId) => await Component(componentId, c => c.SeedOnHand);

    private async Task<uint> Component(
        string componentId, System.Linq.Expressions.Expression<Func<Domain.Models.Materials.Component, uint>> select)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        return await context.Components
            .AsNoTracking()
            .Where(c => c.ComponentId == componentId)
            .Select(select)
            .SingleAsync();
    }

    private async Task<(int Products, int Components, int DeadLetters)> CatalogCounts()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        return (
            await context.Products.CountAsync(),
            await context.Components.CountAsync(),
            await context.DeadLetters.CountAsync());
    }
}
