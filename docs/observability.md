# Observability

How to see what the factory is doing — and how to answer "what happened to work order X?" when
somebody asks.

Written for someone who has never seen this system. If you only read one section, read
[What happened to work order X?](#what-happened-to-work-order-x).

---

## The four surfaces, and the question each one owns

There are four ways to ask about a work order, and they are deliberately not interchangeable.
Picking the right one is most of the skill.

| Question | Surface | Where |
|---|---|---|
| *What happened to this order?* | **Timeline** — the business narrative, in the factory's own words | `GET /work-orders/{id}/timeline` |
| *Why did it fail?* | **Logs** — structured, correlated, with the detail (`component HAW-SVO-002, four on the shelf`) | Grafana → Loki, or the console |
| *Where did the time go?* | **Traces** — one order's whole journey as a waterfall across both services | Grafana → Tempo |
| *Is the factory healthy?* | **Metrics** and the **health probes** | Grafana dashboard, `GET /system/stats`, `/health/ready` |

The timeline is the *business* answer, built from records the factory already persists. The other
three are the *machine* answer. Keeping them apart is on purpose: the visitor-facing dashboard
should never have to render a span waterfall to say an order passed inspection.

Two ids tie it all together, and neither replaces the other:

```
correlation id ──┬── log lines   (a field: {correlation_id="b1f3…"})
                 └── spans       (baggage → the artificeworks.correlation_id attribute)
trace id ────────┬── spans       (the waterfall)
                 └── log lines   (attached automatically by the OTel logging provider)
```

The **correlation id** is the human-facing one — a Guid a visitor can read out loud, returned on
every response as `X-Correlation-ID` and accepted on every request. The **trace id** is the
machine's, and carries causality. From any log line you can reach the trace; from any span you can
reach the lines.

---

## Local setup

```bash
docker compose up -d          # postgres, rabbitmq, redis, otel-lgtm
dotnet ef database update --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api
dotnet run --project src/ArtificeWorks.Api
dotnet run --project src/ArtificeWorks.Workers
```

| Container | Port | What it is for |
|---|---|---|
| `artificeworks-postgres` | 5432 | The factory's state, plus the outbox and dead-letter tables |
| `artificeworks-rabbitmq` | 5672, **15672** (management UI) | The bus; the management UI shows the retry ladder's delay queues |
| `artificeworks-redis` | 6379 | Reserved for a later epic |
| `artificeworks-otel-lgtm` | **3000** (Grafana), 4317/4318 (OTLP) | Tempo + Loki + Prometheus + Grafana in one image |

Open **<http://localhost:3000>** — anonymous admin, no login. The checked-in dashboard is in the
**ArtificeWorks** folder: *Factory Floor*.

### Confirming telemetry is flowing

1. `POST /work-orders`, then `POST /work-orders/{id}/advance` once.
2. Grafana → **Explore** → Tempo → *Search* → service `artificeworks.api`. A trace should appear
   within a few seconds, with spans from `artificeworks.workers` in the same waterfall.
3. Grafana → **Explore** → Prometheus → query `artificeworks_work_orders_by_status`. Non-empty.
4. Grafana → **Explore** → Loki → `{service_name="artificeworks.api"}`. Lines, with fields.

**Nothing here is required to run the system.** Telemetry is never load-bearing: with `otel-lgtm`
stopped, both hosts start normally, the pipeline runs normally, and `GET /system/stats` still
answers. Only the graphs go away.

To turn export off entirely, blank the endpoint:

```json
"Telemetry": { "OtlpEndpoint": "" }
```

Other knobs live in the same section — `SamplingRatio` (1.0 by default; a demo factory does
single-figure orders a minute), `IncludeDbStatements` (on in Development only — the statement text
is most of a DB span's value and also the thing you don't ship to a hosted backend by accident),
and `SnapshotIntervalMs`.

---

## What happened to work order X?

Worked end to end. You start with an order id and nothing else.

### 1. The business answer — start here

```bash
curl localhost:5000/work-orders/$ORDER/timeline
```

One flat chronological array: state changes, the pick, the build, verdicts per unit, the shipment.
For most questions this is the whole answer, and it needs no observability stack at all.

If the order is stuck, this tells you *which stage* it is stuck in. That determines everything
below.

### 2. Get the correlation id

Every response carries `X-Correlation-ID`, so the fastest route is to have kept it. If you did not,
the dead-letter and outbox rows both store it:

```sql
SELECT "CorrelationId", "EventType", "ParkedUtc", "LastError"
FROM dead_letters WHERE "WorkOrderId" = 'ORDER-ID' ORDER BY "ParkedUtc" DESC;

SELECT "CorrelationId", "EventType", "SentUtc", "TraceParent"
FROM outbox_messages WHERE "Payload" LIKE '%ORDER-ID%' ORDER BY "Id";
```

That second query is also how you get a **trace id** directly: `TraceParent` is a W3C
`traceparent`, and the trace id is the second dash-delimited field
(`00-<trace-id>-<span-id>-01`).

### 3. Pivot to the logs

Grafana → **Explore** → Loki:

```logql
{service_name=~"artificeworks.*"} | json | CorrelationId = "YOUR-CORRELATION-ID"
```

One query, both services, in order. Narrow to what went wrong:

```logql
{service_name=~"artificeworks.*"} | json | CorrelationId = "YOUR-CORRELATION-ID" | detected_level =~ "warn|error"
```

Or, without any stack at all, the console the hosts are already printing to — the correlation id
prefixes every line, so `grep` works and is the fallback when the observability stack itself is the
thing that broke.

### 4. Pivot to the trace

Grafana → **Explore** → Tempo. Search by attribute:

```
artificeworks.work_order_id = "YOUR-ORDER-ID"
```

or `artificeworks.correlation_id`, or paste the trace id from step 2.

What you should see: one trace, spanning both services, with `publish work-order.scheduled` →
`process work-order.scheduled` → the picking work → `publish work-order.materials-reserved` → and
so on to `publish work-order.completed`. **If a trace ends at a commit, that is a bug** — trace
context is captured on the outbox row and restored by the dispatcher precisely so it doesn't.

Things worth knowing when reading one:

- **A retried delivery joins the original trace.** It appears as further `process …` spans with a
  higher `artificeworks.attempt`, not as three unrelated traces.
- **A trace can legitimately stay open for minutes.** The retry ladder waits 5s, then 30s, then 2m.
- **A replayed dead letter gets a *new* trace.** It is a new attempt at old work, days later; the
  original trace is long closed. Find it by correlation id, which is preserved across the replay.

### 5. Is it the factory or the machinery?

```bash
curl localhost:5000/system/stats
curl localhost:5000/health/ready
curl localhost:5000/system/dead-letters
```

---

## If you see X, look at Y

| Symptom | What it means | Where to look |
|---|---|---|
| **Outbox lag climbing** (`artificeworks_outbox_lag_seconds`) | The broker is unreachable or slow. **Nothing is lost** — 8.1's design is that a backlog is safe and drains when the broker returns. | RabbitMQ management UI (15672); `/health/ready` reports the broker Degraded |
| **Unsent outbox rows steady at zero, orders still stuck** | Events are going out and nothing is consuming them | Worker running? `GET /health/ready` on the worker (`localhost:5081`); queue bindings in the management UI |
| **`artificeworks_messages_parked_total` moving** | A message exhausted the retry ladder, or was poison. It waits forever for a human — no TTL, no expiry. | `GET /system/dead-letters`, then `POST /system/dead-letters/{id}/replay` |
| **`artificeworks_messages_retried_total` moving, parks flat** | The pipeline is *recovering*. Usually a database blip or a concurrency conflict being replayed. | Logs at Warning for the failing event type |
| **An order stuck in one stage** | Depends which. OnHold = a business decision (short stock, carrier refusal) waiting on a human `POST /release`. Otherwise a message never arrived. | The timeline endpoint first; then the trace |
| **`artificeworks_work_orders_by_status{status="Fault"}` non-zero** | The rebuild cap was exhausted. Terminal until someone intervenes. | Logs at Error; the timeline's inspection entries |
| **Readiness Unhealthy** | Postgres down, or pending migrations | The per-check JSON body says which |
| **Readiness Degraded** | Broker unreachable, or outbox backlog — both deliberately *not* failures | See row one |

---

## Health probes

| Endpoint | Checks | Meaning |
|---|---|---|
| `GET /health/live` | **nothing** | The process is up. A failure means *restart me* — and restarting the API does not fix a dead database, which is why it checks nothing. |
| `GET /health/ready` | Postgres, pending migrations, RabbitMQ, outbox lag | Can it serve? 503 when Postgres is down or migrations are pending. |
| `GET /health` | as `/health/ready` | Kept as an alias so nothing already pointing at it breaks. |

The worker exposes the same two at `http://localhost:5081/health/…` (configurable via
`WorkerHealth:Prefix`; blank it to switch the endpoint off).

Both return per-check status, duration and description as JSON, not a bare string.

**Two deliberate non-failures.** An unreachable broker and an outbox backlog are **Degraded**, never
Unhealthy. Both are symptoms of a problem that taking this instance out of rotation cannot fix —
and doing so would stop new work being *recorded* while achieving nothing. The outbox exists so a
broker outage is a delay rather than a loss.

---

## Metrics reference

Names follow OpenTelemetry conventions (dots, units on the instrument). All of them are declared in
one file — `src/ArtificeWorks.Application/Observability/ArtificeWorksMetrics.cs` — because a metric
name is a public contract with Grafana and with Epic 11's dashboard.

Prometheus mangles them: dots become underscores and counters gain `_total`. So
`artificeworks.messages.retried` is queried as `artificeworks_messages_retried_total`.

| Instrument | Kind | Notable tags |
|---|---|---|
| `artificeworks.work_orders.created` | counter | `origin` |
| `artificeworks.work_orders.transitions` | counter | `from`, `to`, `origin` |
| `artificeworks.materials.picks` | counter | `outcome` |
| `artificeworks.units.built` / `.passed` / `.scrapped` | counter | — |
| `artificeworks.production.rework_attempts` | counter | — |
| `artificeworks.shipments.booked` / `.dispatched` / `.refused` | counter | `carrier` |
| `artificeworks.messages.handled` | counter | `event_type`, `outcome` |
| `artificeworks.messages.retried` | counter | `event_type`, `rung` |
| `artificeworks.messages.parked` / `.replayed` | counter | `event_type` |
| `artificeworks.outbox.published` | counter | `event_type` |
| `artificeworks.messages.paced` | counter | `event_type`, `rung` |
| `artificeworks.world.orders_retired` / `.components_restocked` | counter | — |
| `artificeworks.messages.handling.duration` | histogram (ms) | `event_type`, `outcome` |
| `artificeworks.outbox.publish.duration` | histogram (ms) | `event_type` |
| `artificeworks.outbox.unsent` | gauge | — |
| `artificeworks.outbox.lag` | gauge (s) | — |
| `artificeworks.dead_letters.unreplayed` | gauge | — |
| `artificeworks.work_orders.by_status` | gauge | `status` |
| `artificeworks.world.stock_level_ratio` | gauge | — |

The last five rows and the two `origin` tags are Epic 10. `origin` (`Visitor`/`Simulated`) is a
two-valued dimension — the kind a metric *does* get — so a dashboard can subtract simulated traffic
from anything that looks like a business number. `world.stock_level_ratio` (1.0 = full shelves) is
the most watchable: it falls as orders are picked and snaps back when the world sweep restocks. A
paced order also stamps `artificeworks.paced_ms` on its **producer span** (not a metric), so the
seconds-wide gap between publish and process in a Tempo waterfall reads as explained rather than as
a stall.

Plus the framework's own free meters: ASP.NET Core, HttpClient, EF Core, and `System.Runtime`
(GC, threadpool).

**There are no per-order labels, and there never will be.** Order id, correlation id and trace id
are unbounded cardinality and would ruin a metrics backend. Those questions belong to traces and
logs — which is the whole division of labour this page is describing.

### Queue depth is deliberately not one of ours

There is no `artificeworks.queue.depth`. RabbitMQ already publishes queue depth better than we
could, and the AMQP client does not expose it without a declare-passive round trip per queue — a
poller we would have to write, run and explain.

- **Right now:** the RabbitMQ management UI at <http://localhost:15672> shows depth for
  `artifice.workers`, the three `artifice.retry.*.queue` delay queues, and `artifice.parked`.
  Watching a message climb the ladder there is genuinely the clearest demo of Epic 8's retry logic.
- **When it needs to be in Grafana:** enable RabbitMQ's built-in Prometheus plugin
  (`rabbitmq_prometheus`, port 15692) and scrape it. That is a compose change, not code.

The numbers the demo actually needs — parked messages, unreplayed dead letters, outbox backlog —
are rows in *our* database, and those are gauges above.

The gauges are backed by a **cached snapshot** refreshed every few seconds by a background service,
not by a query inside the metric callback. No metric collection issues a database query, so a
Grafana refresh interval can never become a load generator against Postgres.

### `GET /system/stats`

The same numbers as plain JSON, from the same snapshot, so the endpoint and Grafana cannot
disagree:

```json
{
  "asOfUtc": "2026-07-22T16:04:11Z",
  "fresh": true,
  "workOrdersByStatus": { "Intake": 2, "InProcess": 1, "Completed": 14 },
  "workOrdersTotal": 17,
  "workOrdersInFlight": 3,
  "outboxUnsent": 0,
  "outboxLagSeconds": 0,
  "deadLettersUnreplayed": 1,
  "messagesHandledSinceStart": 96,
  "messagesRetriedSinceStart": 2,
  "messagesParkedSinceStart": 1,
  "messagesReplayedSinceStart": 0,
  "outboxPublishedSinceStart": 96
}
```

It exists so Epic 11's dashboard never has to speak PromQL, embed Grafana auth, or care whether a
metrics backend is running. It sits under `/system` alongside `/system/dead-letters` so the admin
gate deferred since 8.3 falls on one path prefix.

> **No auth yet.** `/system/*` is unauthenticated on purpose while the demo is people pressing
> recovery buttons. `/health/*` is meant to be unauthenticated permanently.

---

## The one non-obvious thing: the outbox and traces

Worth understanding, because it is the piece that makes everything above work.

Epic 8 moved every publish from *after* the commit to *staged inside* it, and a background
dispatcher puts the row on the wire up to a second later — on another thread, with no ambient
activity.

A default OpenTelemetry setup therefore produces something *worse* than no tracing: every trace
ends neatly at the commit, and the dispatcher starts a fresh, parentless one-span trace for each
publish. Fully instrumented, entirely disconnected, and it looks correct right up until you try to
follow an order across a stage boundary.

The fix is the same one the outbox row already applies to the correlation id: **captured at stage
time, restored at publish time.** `outbox_messages.TraceParent` holds the W3C context of the
activity the row was staged in; `OutboxDispatcher` parses it back and publishes under it. A row
staged with no ambient activity publishes untraced — never broken.

The context lives in a *column*, not in the event payload, because 8.3 replays payloads verbatim: a
replayed event carrying a stale, long-closed trace would be worse than one carrying none.

---

## See also

- [docs/messaging-topology.md](messaging-topology.md) — exchanges, queues, the retry ladder, the
  outbox and dead letters. That document owns *how messages move*; this one owns *how you watch
  them*.
- [docs/Plan/EPIC 9 - Observability/](Plan/EPIC%209%20-%20Observability/) — the stories and the
  decisions behind them.
