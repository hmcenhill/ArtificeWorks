# Architecture

The stable shape of ArtificeWorks and the rules it obeys. Read this once to understand *how the
system is built and why*; read the two runbooks when you need the detail:

- **[messaging-topology.md](messaging-topology.md)** — the broker: exchanges, queues, bindings, the
  outbox, the retry ladder, dead letters, pacing, the correlation id's contract.
- **[observability.md](observability.md)** — the runbook: traces, metrics, logs, health probes, and
  "what happened to work order X?" worked end to end.

> This file holds the **load-bearing invariants** — the rules that new work must not break. They
> were paid for across Epics 4–10; each has a reason written down so it doesn't look like a
> regression later. When a rule changes, change it *here*. `HANDOFF.md` tracks what's in flight;
> this tracks what's settled.

---

## Layers

Clean architecture, .NET 10. Dependencies point inward only.

| Project | Role | Depends on |
|---|---|---|
| `ArtificeWorks.Domain` | Aggregates, state machines, value objects. No I/O. | nothing |
| `ArtificeWorks.Application` | Handlers, workflow services, DTOs, repository + publisher interfaces. Also **telemetry declarations** (see below). | Domain |
| `ArtificeWorks.Infrastructure` | EF Core (Postgres) repositories + migrations, RabbitMQ, the outbox dispatcher, telemetry *registration*. | Application, Domain |
| `ArtificeWorks.Api` | ASP.NET Core controllers, Swagger, health, ProblemDetails, idempotency filter. | all |
| `ArtificeWorks.Workers` | Event consumers; drives the pipeline. Publisher too. | all |
| `ArtificeWorks.Simulation` | Publishes and schedules; **consumes nothing**. Pacing, demand, world lifecycle. | all |

Core domain: the `WorkOrder` state machine — Intake → Scheduled → InProcess → Inspection →
Delivery → Completed, with OnHold / Fault / Cancelled off to the side. `Product` (the design),
`Component` (input material), serialized `StockKeepingUnit` (a finished unit), `MaterialReservation`
and `Shipment` as sibling aggregates.

## The three hosts

The API, the Workers, and the Simulation host are separate processes over one Postgres and one
RabbitMQ. **The Workers host drives the pipeline**: one `POST /work-orders/{id}/advance` schedules an
order, and every stage after that is triggered by the event the previous stage published. The
Simulation host adds a clock (pacing), demand (order generation) and a world lifecycle (restock +
retire) on top — it never consumes, so it can never stall the pipeline.

The pipeline, stage by stage:

```
Scheduled ──► [pick materials] ──► MaterialsReserved
          ──► [build units]    ──► ProductionCompleted
          ──► [inspect]        ──► InspectionPassed / ReworkRequired ──┐
          ──► [book + dispatch]──► WorkOrderCompleted                  │
                        ▲                                              │
                        └──────── rework loop (bounded) ◄──────────────┘
```

---

## Load-bearing invariants

### Messaging & reliability

- **Stage the event before the save, never after it.** Every publisher `Add`s an `outbox_messages`
  row to the caller's `DbContext` and does *not* save; the event is flushed by whatever
  `SaveChanges` commits the work. The `OutboxDispatcher` (~1s poll, `FOR UPDATE SKIP LOCKED`, both
  the API and Workers hosts) publishes it. This is the whole reason a stage's work and its
  announcement can't diverge.
- **Publishing is at-least-once, on purpose.** The dispatcher can die between `BasicPublish` and
  marking the row sent, so an event can go out twice. That is the correct trade: a duplicate is
  answered by the dedupe keys below; a loss was answered by nothing. If a future stage needs a new
  guard to survive a duplicate, that is a finding about *its key*, not a reason to weaken the outbox.
- **The dedupe key follows the thing that must happen once.** This is the single rule behind four
  different-looking implementations: picking is order-scoped (`material_reservations.WorkOrderId`
  unique — the reservation row *is* the key); production/inspection are attempt-scoped
  (`production_runs` / `inspection_runs` unique on `(WorkOrderId, AttemptNumber)`, the attempt
  derived from the event so a redelivery computes the same key); shipping went back to order-scoped
  (`shipments.work_order_id`); the client edge is request-scoped (`Idempotency-Key`). Each key row is
  written in the *same* `SaveChanges` as the work it guards, so a losing duplicate rolls back whole.
- **Ordering is per-host, not global.** Each dispatcher claims and publishes in id order, so one
  host's events leave in the order it wrote them; the two dispatchers are not coordinated. Benign
  today because consecutive events for one order are separated by a delivery and a handler. Revisit
  only if a host is scaled out *and* a stage publishes two events for one order.
- **The retry ladder is fixed at three rungs and never gives up quietly.** Transient failures climb
  a broker-native 5s/30s/2m ladder (three fanout exchanges — a delay queue dead-letters under the
  message's own routing key, so the rung must be in the exchange or the event type is lost). Poison
  messages and unknown routing keys park *immediately*. A parked message waits forever for a human
  to replay it — silence is the failure mode Epic 8 removed — which does mean `dead_letters` grows
  unboundedly if nobody looks.

### Simulation & tuning

- **Pacing is quantized and applied only in the outbox dispatcher.** A stage's duration snaps to the
  nearest rung of a fixed ladder (`artifice.pace.*`), and jitter chooses *which* rung. Off is the
  shipped default. Small edits to a duration may move nothing, and a message already in a delay queue
  keeps its old timing — both correct; `PUT /system/simulation` returns the resolved rung so neither
  reads as a bug. The pace ladder is declared on connect by **every** host (the dispatcher runs in
  all three; publishing to a missing exchange closes the channel).
- **The dials are eventually consistent; the row is the authority, appsettings is its default.** A
  `simulation_settings` singleton row, read through a cached snapshot (`SimulationSettingsCache`) in
  all three hosts, seeded from appsettings on first run and never stomped. A `PUT` reaches every host
  within one refresh (~5s); the response says when it lands. This is **global tuning, not per-order
  failure injection** (that is Epic 12, with a different blast radius). **Seeds stay in
  configuration** — a live-editable seed is a flake waiting to happen.
- **The world reset is a sweep, not a truncate.** It restocks components to `seed_on_hand` (a
  conditional set that only ever *raises* stock, so it stays idempotent and never drops below a live
  reservation) and retires old terminal/held/faulted orders — and refuses to touch anything in
  flight, the catalog, or `dead_letters`, which is what makes "without downtime" fall out for free.
  It does not reset the settings row. Restock is ordered by component id, the same anti-deadlock rule
  picking follows, because it is the second bulk writer against `components`.
- **`WorkOrder.Origin` (`Visitor` / `Simulated`) is a real, indexed column and a two-valued metric
  dimension.** Without it every panel reports robot traffic as demand. The generator creates orders
  **over HTTP** (`POST /work-orders` with an `Idempotency-Key`), never by a direct write — a direct
  write would skip the idempotency filter, DTO validation and the outbox row.

### Observability

- **Telemetry is never load-bearing, and defaults enforce it.** `Telemetry:OtlpEndpoint` is empty in
  the shipped config — instruments and activities are real, nothing leaves the process. No collector
  reachable is a startup warning; a blocked exporter has a 5s ceiling; the worker's health listener
  failing to bind is a warning, not a stop. **The pipeline's reliability guarantees must never
  acquire a dependency on the thing that watches them.**
- **A metric never gets a per-order label.** Order id, correlation id and trace id are unbounded
  cardinality and belong to logs and traces; `Visitor`/`Simulated` is the one bounded label metrics
  take. Gauges read a cached `PipelineSnapshot`, so **no metric collection issues a database query**
  (or a Grafana refresh interval becomes a load generator).
- **`ArtificeWorksMetrics` and `ArtificeWorksTelemetry` live in Application, not Infrastructure.** The
  workflow services that record them are in Application and it cannot reference Infrastructure. The
  OTel *registration* is in Infrastructure. Metrics are counted **after** the commit that made the
  thing true, so a losing duplicate counts nothing.
- **Health: liveness checks nothing; an unreachable broker or an outbox backlog is Degraded, not
  Unhealthy.** A failing liveness probe means *restart me*, and restarting the API doesn't fix a dead
  database. The broker and the backlog are Degraded because the outbox makes both a delay rather than
  a loss — removing the instance would stop new work being recorded while fixing nothing.

---

## Known simplifications (deliberate, revisited later)

- **Rebuilds consume no new materials** (decided Epic 6). Physically a scrapped unit burns its parts,
  but `material_reservations.WorkOrderId` is unique, so a second pick per order is impossible without
  reopening that design. The honest fix — widening the reservation key to `(WorkOrderId,
  AttemptNumber)` to match the run tables — waits for **Epic 13** (multi-level BOMs).
- **Release re-triggers exactly one thing, and only at Delivery** (Epic 7). A release that lands in
  Delivery *with no shipment* republishes `InspectionPassed`; every other release is inert. The
  simulation never releases a hold, so refusal stays uncapped and the world sweep retires whatever
  nobody rescues. Re-open only if a future epic adds an automatic recovery action.
- **`SetStatus` (superuser override) has no endpoint**, and `/system/*` (dead letters, stats,
  simulation) is unauthenticated — both wait behind the admin auth gate, which is why `/system` is a
  single path prefix rather than a scatter of endpoints.
