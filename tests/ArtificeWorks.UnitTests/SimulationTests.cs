using ArtificeWorks.Application.Inspection;
using ArtificeWorks.Application.Shipping;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Scheduling;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// Epic 10's decisions, at the level they can be decided: the ladder's arithmetic, the policy's
/// on/off contract, the validation bounds, and the origin default. The parts that need a broker or
/// a database are proved in the integration suite.
/// </summary>
public class PaceLadderTests
{
    /// <summary>
    /// The 8.1 footgun, checked so it stays fixed. A non-empty default array plus a configured one
    /// does not replace — the binder writes into the array that is there — so the default lives
    /// behind the property rather than in it.
    /// </summary>
    [Fact]
    public void An_unconfigured_ladder_uses_the_shipped_rungs_and_a_configured_one_replaces_them()
    {
        Assert.Equal([1_000, 2_000, 3_000, 5_000, 8_000, 13_000, 21_000, 34_000], new PaceConfiguration().Rungs);

        var configured = new PaceConfiguration { RungsMs = [100, 200] };
        Assert.Equal([100, 200], configured.Rungs);
    }

    [Theory]
    [InlineData(5, 3)]     // exactly a rung
    [InlineData(4.6, 3)]   // nearer 5s than 3s
    [InlineData(4, 2)]     // equidistant between 3s and 5s — the lower rung wins, deterministically
    [InlineData(30, 7)]    // beyond the top rung, clamped to 34s
    [InlineData(0.1, 0)]   // below the bottom rung, clamped to 1s
    public void A_duration_snaps_to_the_nearest_rung(double seconds, int expectedRung)
    {
        var rung = new PaceConfiguration().RungFor(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expectedRung, rung);
    }

    /// <summary>Zero means "don't pace this stage" — which is how one stage is switched off without a second flag.</summary>
    [Fact]
    public void A_zero_duration_selects_no_rung_at_all()
    {
        Assert.Null(new PaceConfiguration().RungFor(TimeSpan.Zero));
        Assert.Null(new PaceConfiguration().RungFor(TimeSpan.FromSeconds(-5)));
    }

    /// <summary>The labels are part of queue names, so they are a wire contract rather than a nicety.</summary>
    [Fact]
    public void Rung_names_are_stable_and_appear_in_the_exchange_and_queue()
    {
        var pace = new PaceConfiguration();

        Assert.Equal("5s", pace.LabelFor(3));
        Assert.Equal("artifice.pace.5s", pace.ExchangeFor(3));
        Assert.Equal("artifice.pace.5s.queue", pace.QueueFor(3));
    }

    /// <summary>
    /// Pacing off is the shipped default, and off must mean off for <em>everything</em> — that is
    /// what makes 8.1's dispatcher behaviour byte-for-byte unchanged for a fresh clone.
    /// </summary>
    [Fact]
    public void With_pacing_off_no_event_is_paced()
    {
        var policy = NewPolicy(new SimulationSettings());

        foreach (var eventType in AllStages)
        {
            Assert.Null(policy.Decide(eventType));
        }
    }

    [Fact]
    public void With_pacing_on_each_stage_event_lands_on_its_rung()
    {
        // Jitter off, so this asserts the mapping rather than the coin flip.
        var policy = NewPolicy(new SimulationSettings { PacingEnabled = true, PaceJitter = 0 });

        Assert.Equal("5s", policy.Decide("work-order.scheduled")!.Label);
        Assert.Equal("13s", policy.Decide("work-order.materials-reserved")!.Label);
        Assert.Equal("5s", policy.Decide("work-order.production-completed")!.Label);
        Assert.Equal("8s", policy.Decide("work-order.rework-required")!.Label);
        Assert.Equal("3s", policy.Decide("work-order.inspection-passed")!.Label);
        Assert.Equal("2s", policy.Decide("work-order.shipment-scheduled")!.Label);
    }

    /// <summary>
    /// Announcements are not hand-offs: nothing is waiting to do work because of them, so there is
    /// nothing to pace. Pacing <c>work-order.completed</c> would delay the news, not the work.
    /// </summary>
    [Theory]
    [InlineData("work-order.created")]
    [InlineData("work-order.completed")]
    [InlineData("work-order.faulted")]
    [InlineData("work-order.invented")]
    public void Announcements_and_unknown_events_are_never_paced(string eventType)
    {
        var policy = NewPolicy(new SimulationSettings { PacingEnabled = true });
        Assert.Null(policy.Decide(eventType));
    }

    /// <summary>
    /// Jitter picks a <em>rung</em>, not a millisecond count — the trade the ladder buys. A 5s
    /// stage with wide jitter should be seen on more than one rung, and never off the ladder.
    /// </summary>
    [Fact]
    public void Jitter_moves_a_stage_between_neighbouring_rungs()
    {
        var policy = NewPolicy(
            new SimulationSettings { PacingEnabled = true, PaceJitter = 0.5 }, seed: 1234);

        var labels = Enumerable.Range(0, 200)
            .Select(_ => policy.Decide("work-order.scheduled")!.Label)
            .ToHashSet();

        Assert.True(labels.Count > 1, $"Expected jitter to reach more than one rung; saw [{string.Join(", ", labels)}]");
        Assert.All(labels, label => Assert.Contains(label, new[] { "2s", "3s", "5s", "8s" }));
    }

    /// <summary>Turning the dial mid-flight is the whole of 10.2, so the policy must not cache it.</summary>
    [Fact]
    public void Turning_pacing_on_at_runtime_takes_effect_without_a_new_policy()
    {
        var cache = new SimulationSettingsCache();
        var policy = new PacePolicy(Options.Create(new PaceConfiguration()), cache);

        Assert.Null(policy.Decide("work-order.scheduled"));

        cache.Update(new SimulationSettings { PacingEnabled = true, PaceJitter = 0 });

        Assert.Equal("5s", policy.Decide("work-order.scheduled")!.Label);
    }

    private static readonly string[] AllStages =
    [
        "work-order.scheduled",
        "work-order.materials-reserved",
        "work-order.production-completed",
        "work-order.rework-required",
        "work-order.inspection-passed",
        "work-order.shipment-scheduled",
    ];

    private static PacePolicy NewPolicy(SimulationSettings settings, int? seed = null)
    {
        var cache = new SimulationSettingsCache();
        cache.Update(settings);
        return new PacePolicy(Options.Create(new PaceConfiguration()), cache, seed);
    }
}

public class SimulationSettingsTests
{
    /// <summary>
    /// A fresh clone must behave exactly as Epic 9 left it: nothing paced, nothing generated,
    /// everything passing, every carrier accepting.
    /// </summary>
    [Fact]
    public void The_shipped_defaults_change_nothing_about_epic_nines_behaviour()
    {
        var defaults = SimulationSettings.ShippedDefaults;

        Assert.False(defaults.PacingEnabled);
        Assert.False(defaults.GenerationEnabled);
        Assert.Equal(0, defaults.FailureRate);
        Assert.Equal(0, defaults.RefusalRate);
        Assert.True(defaults.AutoInspect);
        Assert.True(defaults.AutoBook);
        Assert.Equal(3, defaults.MaxRebuildAttempts);
    }

    [Theory]
    [InlineData(nameof(SimulationSettings.FailureRate))]
    [InlineData(nameof(SimulationSettings.RefusalRate))]
    [InlineData(nameof(SimulationSettings.PaceJitter))]
    public void A_rate_outside_zero_to_one_is_rejected(string knob)
    {
        var settings = knob switch
        {
            nameof(SimulationSettings.FailureRate) => new SimulationSettings { FailureRate = 1.5 },
            nameof(SimulationSettings.RefusalRate) => new SimulationSettings { RefusalRate = -0.1 },
            _ => new SimulationSettings { PaceJitter = 2 },
        };

        Assert.NotNull(SimulationSettingsService.Validate(settings));
    }

    [Fact]
    public void An_absurd_pacing_duration_is_rejected_but_zero_is_not()
    {
        Assert.NotNull(SimulationSettingsService.Validate(
            new SimulationSettings { PaceSecondsScheduled = SimulationSettingsService.MaxPaceSeconds + 1 }));

        // Zero is legal: it means "don't pace this stage".
        Assert.Null(SimulationSettingsService.Validate(new SimulationSettings { PaceSecondsScheduled = 0 }));
    }

    [Fact]
    public void A_non_positive_interval_is_rejected()
    {
        Assert.NotNull(SimulationSettingsService.Validate(new SimulationSettings { GenerationIntervalSeconds = 0 }));
        Assert.NotNull(SimulationSettingsService.Validate(new SimulationSettings { WorldSweepIntervalHours = 0 }));
        Assert.NotNull(SimulationSettingsService.Validate(new SimulationSettings { RetireAfterHours = -1 }));
    }

    [Fact]
    public void The_shipped_defaults_are_themselves_valid()
        => Assert.Null(SimulationSettingsService.Validate(SimulationSettings.ShippedDefaults));

    /// <summary>
    /// The dials reach the two coin flips they are supposed to reach, live. Both sources read the
    /// cache on every call rather than capturing it, which is what makes a <c>PUT</c> visible in
    /// the next order rather than the next restart.
    /// </summary>
    [Fact]
    public void Raising_the_failure_rate_at_runtime_changes_the_next_verdict()
    {
        var cache = new SimulationSettingsCache();
        var source = new RandomVerdictSource(new InspectionConfiguration { Seed = 7 }, cache);

        Assert.True(source.Verdict(new StockKeepingUnit(TestData.DefaultProduct())).Passed);

        cache.Update(new SimulationSettings { FailureRate = 1.0 });

        Assert.False(source.Verdict(new StockKeepingUnit(TestData.DefaultProduct())).Passed);
    }

    [Fact]
    public void Raising_the_refusal_rate_at_runtime_changes_the_next_booking()
    {
        var cache = new SimulationSettingsCache();
        var booking = new ConfiguredCarrierBooking(new ShippingConfiguration { Seed = 7 }, cache);

        Assert.Equal(CarrierBookingOutcome.Accepted, booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1)).Outcome);

        cache.Update(new SimulationSettings { RefusalRate = 1.0 });

        Assert.Equal(CarrierBookingOutcome.Refused, booking.Book(new CarrierBookingRequest(Guid.NewGuid(), 1)).Outcome);
    }

    /// <summary>
    /// The fallback that keeps every existing unit test — and a host whose first read has not
    /// happened yet — behaving as configured rather than as a blank record.
    /// </summary>
    [Fact]
    public void With_no_cache_the_startup_configuration_still_applies()
    {
        var source = new RandomVerdictSource(new InspectionConfiguration { FailureRate = 1.0 });
        Assert.False(source.Verdict(new StockKeepingUnit(TestData.DefaultProduct())).Passed);
    }
}

public class WorkOrderOriginTests
{
    /// <summary>
    /// Visitor unless the caller says otherwise — so every path that existed before Epic 10 keeps
    /// producing real demand without being told to, and <c>/system/stats</c> does not silently
    /// reclassify history.
    /// </summary>
    [Fact]
    public void An_order_is_a_visitors_unless_it_says_otherwise()
    {
        var order = new WorkOrder("someone", TestData.DefaultProduct(), 1);
        Assert.Equal(WorkOrderOrigin.Visitor, order.Origin);
    }

    [Fact]
    public void A_generated_order_carries_its_origin_and_never_changes_it()
    {
        var order = new WorkOrder("sim:generator", TestData.DefaultProduct(), 1, null, WorkOrderOrigin.Simulated);

        Assert.Equal(WorkOrderOrigin.Simulated, order.Origin);

        // It survives the transitions that rewrite everything else about the order.
        order.AdvanceToNextStep("someone");
        order.SetHold("someone");
        order.ReleaseHold("someone");

        Assert.Equal(WorkOrderOrigin.Simulated, order.Origin);
    }
}

public class PeriodicTaskHostTests
{
    /// <summary>
    /// The rule the three hand-rolled loops each stated separately, now stated once: a throwing
    /// task never kills its loop. A scheduler that stops is silence, which is the failure mode
    /// Epic 8 removed and Epic 9 made visible.
    /// </summary>
    [Fact]
    public async Task A_throwing_task_keeps_running_on_its_interval()
    {
        var task = new CountingTask(TimeSpan.FromMilliseconds(20)) { Throw = true };
        var host = new PeriodicTaskHost([task], NullLogger<PeriodicTaskHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await WaitUntil(() => task.Runs >= 3);
        await host.StopAsync(CancellationToken.None);

        Assert.True(task.Runs >= 3, $"Expected the loop to survive its own failures; it ran {task.Runs} time(s).");
    }

    /// <summary>
    /// One loop per task, not one loop over all tasks: a six-hourly sweep must not become the pacer
    /// for a five-second refresh, and a hung task must not stop the others.
    /// </summary>
    [Fact]
    public async Task A_slow_task_does_not_hold_up_a_fast_one()
    {
        var fast = new CountingTask(TimeSpan.FromMilliseconds(20));
        var slow = new CountingTask(TimeSpan.FromMilliseconds(20)) { Delay = TimeSpan.FromSeconds(30) };

        var host = new PeriodicTaskHost([slow, fast], NullLogger<PeriodicTaskHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await WaitUntil(() => fast.Runs >= 5);
        await host.StopAsync(CancellationToken.None);

        Assert.True(fast.Runs >= 5);
        Assert.Equal(1, slow.Runs);
    }

    /// <summary>
    /// A destructive task must not fire on startup, or a crash-restart loop becomes a work loop.
    /// That is why <c>WorldResetTask</c> and <c>OrderGenerator</c> both opt out.
    /// </summary>
    [Fact]
    public async Task A_task_that_opts_out_of_startup_waits_out_its_first_interval()
    {
        var task = new CountingTask(TimeSpan.FromSeconds(30)) { Startup = false };
        var host = new PeriodicTaskHost([task], NullLogger<PeriodicTaskHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(0, task.Runs);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }
    }

    private sealed class CountingTask(TimeSpan interval) : IScheduledTask
    {
        private int _runs;

        public bool Throw { get; init; }
        public bool Startup { get; init; } = true;
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public int Runs => Volatile.Read(ref _runs);

        public string Name => "counting";
        public TimeSpan Interval => interval;
        public bool RunOnStartup => Startup;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Throw)
            {
                throw new InvalidOperationException("Scripted task failure.");
            }
        }
    }
}
