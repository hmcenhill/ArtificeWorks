using System.Net.Http.Json;

using ArtificeWorks.Application.Commands;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Infrastructure.Persistence;
using ArtificeWorks.Infrastructure.Scheduling;
using ArtificeWorks.Infrastructure.Simulation;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.Simulation.Tasks;

/// <summary>
/// Creates work orders at a low rate so a visitor arriving at 3am finds a factory mid-shift rather
/// than an empty board (10.3).
/// <para>
/// <strong>It goes over HTTP, through the front door.</strong> The epic's note is explicit: the
/// simulation rides the same events and consumers as everything else — it is a <em>client</em> of
/// the pipeline, not a parallel implementation, and if it needs a shortcut the pipeline doesn't
/// offer, that is a smell worth examining. A generator writing to <c>work_orders</c> directly would
/// be exactly that shortcut, and would skip 8.4's <c>[Idempotent]</c> filter, the DTO validation and
/// the outbox row that all three exist to guarantee. Going through the front door also makes this a
/// working example for Epic 12's chaos actions and a continuous, unattended smoke test of the
/// create path.
/// </para>
/// <para>
/// <strong>It sends an <c>Idempotency-Key</c>.</strong> The generator is exactly the kind of
/// retrying client 8.4 was built for, so it behaves like one.
/// </para>
/// <para>
/// <strong>It never makes decisions.</strong> It creates orders and nothing else: it does not
/// advance them, does not inspect them, does not choose carriers, and — settled at grooming —
/// <strong>does not release holds</strong>. Every other stage already runs itself; anything held is
/// a visitor's to rescue, which is what keeps 7.3's uncapped carrier refusal defensible rather than
/// turning it into an infinite loop.
/// </para>
/// <para>
/// <strong>Nothing it does can stop anything.</strong> An unreachable API is a logged warning and a
/// skipped tick — never a crash, never a retry storm. Epic 9's rule generalises: the thing that
/// watches or feeds the factory must not be able to take it down.
/// </para>
/// </summary>
public sealed class OrderGenerator : IScheduledTask
{
    /// <summary>The author recorded on every generated order, alongside the persisted <see cref="WorkOrderOrigin"/>.</summary>
    public const string Requestor = "sim:generator";

    private readonly HttpClient _http;
    private readonly SimulationSettingsCache _settings;
    private readonly PipelineSnapshotCache _snapshot;
    private readonly SimulationConfiguration _config;
    private readonly ILogger<OrderGenerator> _logger;
    private readonly Random _random;
    private readonly Lock _gate = new();

    public OrderGenerator(
        HttpClient http,
        SimulationSettingsCache settings,
        PipelineSnapshotCache snapshot,
        IOptions<SimulationConfiguration> config,
        ILogger<OrderGenerator> logger)
    {
        _http = http;
        _settings = settings;
        _snapshot = snapshot;
        _config = config.Value;
        _logger = logger;
        _random = _config.Seed is int seed ? new Random(seed) : new Random();
    }

    public string Name => "order-generator";

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, _settings.Current.GenerationIntervalSeconds));

    /// <summary>
    /// False: a task that creates things must not fire the instant the process starts, or a restart
    /// loop becomes a work loop. It waits out its first interval like everything else.
    /// </summary>
    public bool RunOnStartup => false;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        // Two switches, two jobs: the row says whether the factory should be generating, this says
        // whether this deployment is allowed to — so a second simulation host started by accident
        // cannot quietly double the demand.
        if (!_config.GeneratorEnabled || !settings.GenerationEnabled)
        {
            return;
        }

        var snapshot = _snapshot.Current;
        if (snapshot.IsFresh && snapshot.InFlight >= settings.MaxInFlight)
        {
            // Debug, not Information: this is the normal state of a busy factory, not an event.
            // A ceiling rather than a rate limiter, because the failure it prevents is a backlog
            // built during an outage — and a rate limiter does not prevent that, it just builds the
            // backlog politely.
            _logger.LogDebug(
                "Generation skipped: {InFlight} order(s) in flight, ceiling is {MaxInFlight}.",
                snapshot.InFlight, settings.MaxInFlight);
            return;
        }

        var (productId, quantity) = Choose();
        var request = new CreateWorkOrderRequest
        {
            Requestor = Requestor,
            ItemId = productId,
            Qty = quantity,
            Notes = "Generated by the simulation.",
            Origin = WorkOrderOrigin.Simulated,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "/work-orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Order generation rejected by the API: {StatusCode} for {Qty} × {ProductId}.",
                    (int)response.StatusCode, quantity, productId);
                return;
            }

            _logger.LogInformation(
                "Generated work order for {Qty} × {ProductId} ({InFlight} in flight, ceiling {MaxInFlight}).",
                quantity, productId, snapshot.InFlight, settings.MaxInFlight);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception e)
        {
            // A skipped tick, never a crash and never a retry storm — the next tick tries again.
            _logger.LogWarning(e,
                "Could not reach the API at {BaseAddress} to generate an order; skipping this tick.",
                _http.BaseAddress);
        }
    }

    /// <summary>
    /// A product off the seeded catalog and a plausible quantity. The catalog ids come from
    /// <see cref="CatalogSeeder"/> rather than from a query, because an empty database should make
    /// the generator produce a 400 the log can show, not silently do nothing.
    /// </summary>
    private (string ProductId, uint Quantity) Choose()
    {
        var products = CatalogSeeder.SeededProductIds;

        lock (_gate)
        {
            return (products[_random.Next(products.Count)], (uint)_random.Next(1, 6));
        }
    }
}
