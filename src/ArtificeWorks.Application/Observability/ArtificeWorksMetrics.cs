using System.Diagnostics.Metrics;

namespace ArtificeWorks.Application.Observability;

/// <summary>
/// Every instrument the pipeline emits, declared in one file (9.2).
/// <para>
/// <strong>One file is the point.</strong> Metric names are a public contract — with Grafana, with
/// 9.4's runbook, and with Epic 11's dashboard — and a contract you have to grep six projects to
/// read is one nobody reads. Adding an instrument means editing this file, which means the review
/// sees the whole surface at once.
/// </para>
/// <para>
/// <strong>Counted at the workflow services, never in the domain.</strong> <c>WorkOrder</c> depends
/// on nothing and keeps depending on nothing; the service that commits a transition is the honest
/// place to count it, and is also where the correlation id and the ambient activity already are.
/// A stage transition is counted in exactly one place.
/// </para>
/// <para>
/// <strong>No per-work-order labels. Ever.</strong> Order id, correlation id and trace id are
/// unbounded cardinality and would ruin a metrics backend. Those questions belong to traces and
/// logs — which is precisely the division of labour this epic is arguing for.
/// </para>
/// <para>
/// <strong>The tallies next to the counters.</strong> A <see cref="Counter{T}"/> cannot be read
/// back; <c>GET /system/stats</c> has to answer "how many retries since start?" without a metrics
/// backend running. Each counted event therefore also bumps a plain <see cref="long"/> here, so
/// the endpoint and Grafana are incremented by the same call and cannot drift.
/// </para>
/// </summary>
public sealed class ArtificeWorksMetrics
{
    /// <summary>The meter name to enable in an exporter. Stable — it is in the Grafana dashboard JSON.</summary>
    public const string MeterName = "ArtificeWorks.Pipeline";

    private readonly Counter<long> _ordersCreated;
    private readonly Counter<long> _transitions;
    private readonly Counter<long> _picks;
    private readonly Counter<long> _unitsBuilt;
    private readonly Counter<long> _unitsPassed;
    private readonly Counter<long> _unitsScrapped;
    private readonly Counter<long> _reworkAttempts;
    private readonly Counter<long> _shipmentsBooked;
    private readonly Counter<long> _shipmentsDispatched;
    private readonly Counter<long> _shipmentsRefused;
    private readonly Counter<long> _messagesHandled;
    private readonly Counter<long> _messagesRetried;
    private readonly Counter<long> _messagesParked;
    private readonly Counter<long> _messagesReplayed;
    private readonly Counter<long> _outboxPublished;
    private readonly Histogram<double> _handlingDuration;
    private readonly Histogram<double> _publishDuration;

    private long _retriedTally;
    private long _parkedTally;
    private long _replayedTally;
    private long _outboxPublishedTally;
    private long _handledTally;

    /// <param name="snapshot">
    /// Backs the observable gauges. Their callbacks run on the meter's collection thread, in root
    /// scope, where a scoped <c>DbContext</c> cannot be resolved — and a <c>SELECT</c> per metric
    /// per scrape would let a Grafana refresh interval load the database. They read a cached
    /// snapshot instead, refreshed on a timer by <c>PipelineSnapshotService</c>.
    /// </param>
    public ArtificeWorksMetrics(IMeterFactory meterFactory, PipelineSnapshotCache snapshot)
    {
        var meter = meterFactory.Create(MeterName);

        // ------------------------------------------------- things that happened (counters)

        _ordersCreated = meter.CreateCounter<long>(
            "artificeworks.work_orders.created", "{work_order}", "Work orders accepted by the API.");

        _transitions = meter.CreateCounter<long>(
            "artificeworks.work_orders.transitions", "{transition}",
            "Work order state changes, tagged from/to. The pipeline's throughput, per stage.");

        _picks = meter.CreateCounter<long>(
            "artificeworks.materials.picks", "{pick}", "Material picking attempts, tagged by outcome.");

        _unitsBuilt = meter.CreateCounter<long>(
            "artificeworks.units.built", "{unit}", "Serialized units produced.");

        _unitsPassed = meter.CreateCounter<long>(
            "artificeworks.units.passed", "{unit}", "Units that passed inspection.");

        _unitsScrapped = meter.CreateCounter<long>(
            "artificeworks.units.scrapped", "{unit}", "Units scrapped at inspection.");

        _reworkAttempts = meter.CreateCounter<long>(
            "artificeworks.production.rework_attempts", "{attempt}",
            "Rebuilds started because an attempt fell short. Attempt 1 is not a rework.");

        _shipmentsBooked = meter.CreateCounter<long>(
            "artificeworks.shipments.booked", "{shipment}", "Carrier bookings accepted.");

        _shipmentsDispatched = meter.CreateCounter<long>(
            "artificeworks.shipments.dispatched", "{shipment}", "Parcels handed to the carrier.");

        _shipmentsRefused = meter.CreateCounter<long>(
            "artificeworks.shipments.refused", "{shipment}", "Bookings the carrier refused (7.3).");

        _messagesHandled = meter.CreateCounter<long>(
            "artificeworks.messages.handled", "{message}",
            "Deliveries the consumer classified, tagged by event type and outcome (acked/retried/parked).");

        _messagesRetried = meter.CreateCounter<long>(
            "artificeworks.messages.retried", "{message}",
            "Deliveries pushed onto a rung of the retry ladder, tagged by rung (8.2).");

        _messagesParked = meter.CreateCounter<long>(
            "artificeworks.messages.parked", "{message}",
            "Deliveries that reached artifice.parked — poison, or the ladder exhausted.");

        _messagesReplayed = meter.CreateCounter<long>(
            "artificeworks.messages.replayed", "{message}", "Dead letters a human put back (8.3).");

        _outboxPublished = meter.CreateCounter<long>(
            "artificeworks.outbox.published", "{message}", "Outbox rows successfully put on the wire.");

        // ------------------------------------------------ things that took time (histograms)

        _handlingDuration = meter.CreateHistogram<double>(
            "artificeworks.messages.handling.duration", "ms",
            "Wall time from delivery to ack/retry/park, tagged by event type.");

        _publishDuration = meter.CreateHistogram<double>(
            "artificeworks.outbox.publish.duration", "ms",
            "Wall time to put one outbox row on the wire.");

        // --------------------------------------- things that are true right now (gauges)

        meter.CreateObservableGauge(
            "artificeworks.outbox.unsent", () => snapshot.Current.UnsentOutboxRows, "{message}",
            "Outbox rows written but not yet published. Climbing means the broker is unreachable.");

        meter.CreateObservableGauge(
            "artificeworks.outbox.lag", () => snapshot.Current.OutboxLagSeconds, "s",
            "Age of the oldest unsent outbox row. THE number to alert on — a backlog is safe, a growing one is not.");

        meter.CreateObservableGauge(
            "artificeworks.dead_letters.unreplayed", () => snapshot.Current.UnreplayedDeadLetters, "{message}",
            "Dead letters nobody has replayed. Never decreases on its own — 8.2's ladder gives up loudly, not quietly.");

        meter.CreateObservableGauge(
            "artificeworks.work_orders.by_status",
            () => snapshot.Current.WorkOrdersByStatus
                .Select(entry => new Measurement<long>(entry.Value, new KeyValuePair<string, object?>("status", entry.Key)))
                .ToArray(),
            "{work_order}",
            "Work orders per status. The factory floor at a glance; a stall shows up as a status that stops emptying.");
    }

    // -------------------------------------------------------------------------- recording

    public void WorkOrderCreated() => _ordersCreated.Add(1);

    /// <summary>
    /// One stage transition. Called by whoever commits it, once — double-counting here is the
    /// failure mode 9.2's tests exist to catch.
    /// </summary>
    public void Transition(string from, string to) => _transitions.Add(1,
        new KeyValuePair<string, object?>("from", from),
        new KeyValuePair<string, object?>("to", to));

    public void Pick(string outcome) => _picks.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void UnitsBuilt(int count, int attempt)
    {
        if (count > 0) { _unitsBuilt.Add(count); }
        if (attempt > 1) { _reworkAttempts.Add(1); }
    }

    public void Verdicts(long passed, long scrapped)
    {
        if (passed > 0) { _unitsPassed.Add(passed); }
        if (scrapped > 0) { _unitsScrapped.Add(scrapped); }
    }

    public void ShipmentBooked(string carrier) =>
        _shipmentsBooked.Add(1, new KeyValuePair<string, object?>("carrier", carrier));

    public void ShipmentDispatched(string carrier) =>
        _shipmentsDispatched.Add(1, new KeyValuePair<string, object?>("carrier", carrier));

    public void ShipmentRefused(string carrier) =>
        _shipmentsRefused.Add(1, new KeyValuePair<string, object?>("carrier", carrier));

    /// <summary>One delivery, classified. <paramref name="outcome"/> is acked / retried / parked.</summary>
    public void MessageHandled(string eventType, string outcome, double elapsedMs)
    {
        Interlocked.Increment(ref _handledTally);

        var typeTag = new KeyValuePair<string, object?>("event_type", eventType);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);

        _messagesHandled.Add(1, typeTag, outcomeTag);
        _handlingDuration.Record(elapsedMs, typeTag, outcomeTag);
    }

    public void MessageRetried(string eventType, string rung)
    {
        Interlocked.Increment(ref _retriedTally);
        _messagesRetried.Add(1,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("rung", rung));
    }

    public void MessageParked(string eventType)
    {
        Interlocked.Increment(ref _parkedTally);
        _messagesParked.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
    }

    public void MessageReplayed(string eventType)
    {
        Interlocked.Increment(ref _replayedTally);
        _messagesReplayed.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
    }

    public void OutboxPublished(string eventType, double elapsedMs)
    {
        Interlocked.Increment(ref _outboxPublishedTally);
        _outboxPublished.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
        _publishDuration.Record(elapsedMs, new KeyValuePair<string, object?>("event_type", eventType));
    }

    // ------------------------------------------------- readable tallies, for /system/stats

    public long MessagesHandledSinceStart => Interlocked.Read(ref _handledTally);
    public long MessagesRetriedSinceStart => Interlocked.Read(ref _retriedTally);
    public long MessagesParkedSinceStart => Interlocked.Read(ref _parkedTally);
    public long MessagesReplayedSinceStart => Interlocked.Read(ref _replayedTally);
    public long OutboxPublishedSinceStart => Interlocked.Read(ref _outboxPublishedTally);
}
