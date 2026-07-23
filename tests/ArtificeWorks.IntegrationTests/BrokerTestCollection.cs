namespace ArtificeWorks.IntegrationTests;

/// <summary>
/// The test classes that each stand up their own RabbitMQ container, run <strong>one class at a
/// time</strong>.
/// <para>
/// <strong>Why this exists.</strong> xUnit runs test collections in parallel, and by default every
/// class is its own collection — so four broker-owning classes meant four RabbitMQ containers, four
/// Postgres containers and four full pipelines competing for one Docker host. The tests are all
/// wall-clock tests (a retry rung's TTL, a pace rung's TTL, "did the order reach Completed within
/// 30 seconds?"), and under that much contention the budgets stopped being generous and started
/// being a coin flip. Adding 10.1's <c>PaceLadderTests</c> was what tipped it over.
/// </para>
/// <para>
/// The fix is to stop competing rather than to widen the timeouts: a timeout wide enough to survive
/// four parallel brokers is wide enough to hide a real stall, which is precisely the failure mode
/// Epic 8 and Epic 9 exist to make impossible. Everything that does <em>not</em> need a broker —
/// the API tests, the outbox tests, the picking and production races — keeps running in parallel
/// alongside this collection.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class BrokerTestCollection
{
    public const string Name = "broker";
}
