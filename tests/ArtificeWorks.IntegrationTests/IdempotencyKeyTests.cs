using System.Net;
using System.Net.Http.Json;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Api.Middleware;
using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// <c>Idempotency-Key</c> on <c>POST /work-orders</c> (8.4) — the last unguarded door, and the
/// one edge the system does not control.
/// <para>
/// The epic's throughline lands here: <em>the key follows the thing that must happen once</em>.
/// 5.4 said the order, 6.4 said the attempt, 7.1 said the order again, and here it is the
/// client's request. Four stages, one rule, four different keys.
/// </para>
/// </summary>
public class IdempotencyKeyTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public IdempotencyKeyTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task The_same_key_twice_creates_one_work_order_and_replays_the_first_response()
    {
        await SeedProduct("IDEM-ONE");
        var request = NewRequest("IDEM-ONE");
        var key = Guid.NewGuid().ToString();

        var first = await Post(request, key);
        var second = await Post(request, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Byte-identical, including the created order's id — which is the point. A client that
        // never received the first 201 has no other way to learn it, and a 409 would strand it.
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, secondBody);

        Assert.Equal(first.Headers.Location, second.Headers.Location);

        // Said out loud, so nobody has to infer it.
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));

        Assert.Equal(1, await CountOrdersFor("IDEM-ONE"));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_rejected_rather_than_quietly_replayed()
    {
        await SeedProduct("IDEM-DIFF");
        var key = Guid.NewGuid().ToString();

        var first = await Post(NewRequest("IDEM-DIFF", qty: 1), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await Post(NewRequest("IDEM-DIFF", qty: 9), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(ProblemCodes.IdempotencyKeyReused, await second.ReadProblemCodeAsync());

        Assert.Equal(1, await CountOrdersFor("IDEM-DIFF"));
    }

    [Fact]
    public async Task Two_concurrent_requests_with_one_key_produce_exactly_one_work_order()
    {
        await SeedProduct("IDEM-RACE");
        var request = NewRequest("IDEM-RACE");
        var key = Guid.NewGuid().ToString();

        // Raced against Postgres, the 5.3 split. Both requests pass the pre-check read; the
        // unique index on idempotency_keys.Key is what actually resolves it, and the loser's work
        // order rolls back with its rejected marker.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Post(request, key)));

        Assert.Equal(1, await CountOrdersFor("IDEM-RACE"));

        // Every caller gets a usable answer: the winner's 201, a replay of it, or an honest
        // "still in flight". None of them gets a 500, and none of them gets a second order.
        Assert.All(responses, response => Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected status {response.StatusCode}."));

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);

        // The 201s all describe the same order — the one that exists.
        var created = new List<Guid>();
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            created.Add((await response.Content.ReadFromJsonAsync<WorkOrderDto>())!.Id);
        }
        Assert.Single(created.Distinct());
    }

    [Fact]
    public async Task A_failed_request_does_not_pin_the_key()
    {
        var key = Guid.NewGuid().ToString();

        // No such product: a 400. Storing that against the key would turn a typo into a permanent
        // rejection for a client that sensibly retries with a corrected body.
        var failed = await Post(NewRequest("IDEM-NEVER-SEEDED"), key);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        await SeedProduct("IDEM-RETRY");
        var succeeded = await Post(NewRequest("IDEM-RETRY"), key);

        Assert.Equal(HttpStatusCode.Created, succeeded.StatusCode);
        Assert.Equal(1, await CountOrdersFor("IDEM-RETRY"));
    }

    [Fact]
    public async Task The_key_the_work_order_and_its_event_all_commit_together()
    {
        await SeedProduct("IDEM-ATOMIC");
        var key = Guid.NewGuid().ToString();

        var response = await Post(NewRequest("IDEM-ATOMIC"), key);
        var order = await response.Content.ReadFromJsonAsync<WorkOrderDto>();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        // One transaction holding the work, its announcement, and the marker that says it
        // happened. That is the whole epic in a single commit.
        Assert.NotNull(await context.WorkOrders.AsNoTracking().SingleOrDefaultAsync(wo => wo.Id == order!.Id));
        Assert.NotNull(await context.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Key == key));
        Assert.Contains(await context.OutboxMessages.AsNoTracking().ToListAsync(),
            message => message.EventType == "work-order.created"
                && message.Payload.Contains(order!.Id.ToString()));
    }

    [Fact]
    public async Task A_request_without_the_header_is_entirely_unaffected()
    {
        await SeedProduct("IDEM-NONE");

        var first = await Post(NewRequest("IDEM-NONE"), key: null);
        var second = await Post(NewRequest("IDEM-NONE"), key: null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Two identical requests with no key are two orders, exactly as before. The header is
        // opt-in; the rest of the suite proves the default by never sending it.
        Assert.Equal(2, await CountOrdersFor("IDEM-NONE"));
    }

    // -------------------------------------------------------------------------- helpers

    private static CreateWorkOrderRequest NewRequest(string productId, uint qty = 2) => new()
    {
        Requestor = "Idempotent Tester",
        ItemId = productId,
        Qty = qty,
        Notes = "idempotency"
    };

    private async Task<HttpResponseMessage> Post(CreateWorkOrderRequest request, string? key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/work-orders")
        {
            Content = JsonContent.Create(request)
        };

        if (key is not null)
        {
            message.Headers.Add(IdempotencyFilter.HeaderName, key);
        }

        return await _fixture.Client.SendAsync(message);
    }

    private async Task SeedProduct(string productId)
    {
        var response = await _fixture.Client.PostAsJsonAsync("/products", new CreateProductRequest
        {
            Requestor = "Idempotent Tester",
            ProductId = productId,
            ProductName = $"{productId} Automaton"
        });

        Assert.True(response.IsSuccessStatusCode);
    }

    private async Task<int> CountOrdersFor(string productId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArtificeWorksDbContext>();

        return await context.WorkOrders
            .CountAsync(wo => EF.Property<string>(wo, "ordered_item_id") == productId);
    }
}
