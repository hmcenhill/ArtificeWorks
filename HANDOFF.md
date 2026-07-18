# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line entry to the *Log*; prune anything no longer true. Keep this file under ~80 lines — when a decision becomes permanent, move it to `docs/` (architecture.md or the relevant epic) and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-18 (story 4.2)

## Current state

- Project renamed from EventDrivenOrderProcessingSystem to **ArtificeWorks** (commit `21b1753`): solution, projects, namespaces, `ArtificeWorksDbContext`, DB name, docker containers, CI.
- Plan fully rewritten for the manufacturing pivot (commit `d218f43`): 17 epics / 7 milestones in `docs/Plan/`. **Epics 1–3 complete** — synchronous core done: work-order domain + state machine, catalog/work-order REST API, RFC 7807 ProblemDetails contract, full unit + integration coverage. Per-story detail in `docs/Plan/` and the Log below.
- **Epic 4 (messaging) in progress** — the async pivot.
- **Epic 4 stories reviewed** (start of epic): 4.1/4.2/4.3 confirmed sound; added a locked **Decisions** block to 4.1 and clarified 4.1↔4.3 correlation split + 4.1↔4.2 testing split. See the epic folder.
- **Story 4.1 done** — event contracts + publisher. `Application/Messaging/`: `IntegrationEvent` base, `EventEnvelope<T>`, `IEventPublisher`, `ICorrelationContext`/`CorrelationContext`, `WorkOrderCreated`/`WorkOrderScheduled`. `Infrastructure/Messaging/`: `RabbitMqConfiguration`, lazy singleton `RabbitMqConnection` (declares durable **direct** exchange `artifice.events`), scoped `RabbitMqEventPublisher` (STJ web-defaults, persistent delivery, AMQP `type`/`message_id`/`correlation_id`). Api: `CorrelationMiddleware`. Handler publishes **after commit, best-effort**. API integration tests use `NoOpEventPublisher`.
- **Story 4.2 done** — Workers is now a Generic Host (`Microsoft.NET.Sdk.Worker`) consuming `artifice.events`. `Workers/Consuming/`: `IIntegrationEventHandler<T>` (one handler per event type), `EventRegistration` + `EventDispatcher` (builds its dispatch table from DI, resolves handler in a fresh scope per message), `RabbitMqConsumerService` (`BackgroundService`: declares durable queue `artifice.workers`, binds only handled routing keys, prefetch 1, **manual acks** — ack on success, **nack `requeue:false`** on any handler exception, so the loop survives a poison message; no DLQ yet), and `AddEventConsumer()`/`AddEventHandler<TEvent,THandler>()` (adding a handler is the *only* change needed — routing key read once from the event's own `EventType` via an uninitialized instance). `Workers/Handlers/WorkOrderScheduledHandler` loads the order + `AppendNote(...)` (new domain method) so a consumed event leaves an observable state-history row. Refactored `AddRabbitMqMessaging` → extracted `AddRabbitMqConnection` (connection-only, what the consume-only worker uses). New `WorkerConsumerTests` = real publish→consume E2E (Testcontainers **RabbitMQ + Postgres**). Workers builds in CI via the slnx. **73 tests green (49 unit + 24 integration, +1 worker E2E).**

## Next up

1. **Story 4.3** — harden correlation into log scopes on both services (flow the envelope's `CorrelationId` into the worker's log scope when handling), + write the topology doc. Notes in `docs/Plan/.../4.3.md`.

## Open decisions

- `SetStatus` (superuser override) still has no endpoint — deferred to admin auth, as planned.
- **No optimistic concurrency on `WorkOrder`** (no rowversion / `xmin`). Concurrent *same-order* advances would duplicate history / lose updates silently — so 3.4 tests concurrent *create* only. Deferred; the Epic 4 async pivot moves transitions onto a queue and will revisit aggregate concurrency. (Detailed in `docs/Plan/.../3.4.md`.)
- **`WorkOrderStateHistory.CompletedBy` is silently unmapped by EF** (discovered in 4.2) — it's absent from the model snapshot, so the "who did this transition" author is never persisted (always null in the DB), even though every transition sets it. Latent since the property was added without a migration. Not fixed in 4.2 (out of scope; needs a mapping + migration); the worker E2E test asserts on `Notes` instead. **Worth a small follow-up:** map `CompletedBy` + add a migration.
- **Publishing is best-effort, at-most-once** (4.1). Publish-after-commit with a swallow-and-log on broker failure: a state change can persist while its event is dropped (the classic dual-write gap). Chosen to keep the synchronous core resilient to broker outages. Epic 8's transactional outbox closes it.

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update (see Notes.md).
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

- **2026-07-18** — Story 4.2: worker consumption + dispatch. Workers → Generic Host consuming `artifice.events`; `IIntegrationEventHandler<T>` + `EventDispatcher` + `RabbitMqConsumerService` (manual acks, nack `requeue:false`); `WorkOrderScheduledHandler` appends a state-history note via new `WorkOrder.AppendNote`. Extracted `AddRabbitMqConnection`. Real publish→consume E2E (`WorkerConsumerTests`, Testcontainers RabbitMQ+Postgres). Flagged unmapped `CompletedBy`. 73 tests green.
- **2026-07-18** — Story 4.1: event contracts + RabbitMQ publisher (first async plumbing). `EventEnvelope<T>` + `WorkOrderCreated`/`WorkOrderScheduled` in Application; `RabbitMqConnection`/`RabbitMqEventPublisher` + direct exchange `artifice.events` in Infrastructure; correlation middleware in Api; handler publishes best-effort after commit. Verified end-to-end against a live broker. 72 tests green.
- **2026-07-18** — Story 3.4: integration coverage closed (Epic 3 complete). `ProductApiTests` + work-order additions (history-404, hold/advance rejections, full-lifecycle walk, concurrent-create smoke check); shared `ReadProblemCodeAsync`. 68 tests green. Flagged missing aggregate optimistic-concurrency (concurrent advance untested by design).
- **2026-07-18** — Story 3.3: RFC 7807 ProblemDetails contract with stable `code` per failure; boundary DTO validation; domain rejection codes flow up via `TransitionErrorCode`. Duplicate product create → 409. 59 tests green.
- **2026-07-18** — Story 3.2: cancellation. `Cancelled` terminal status + `cancel` endpoint; terminal guards generalised; Fault kept cancellable; stock released on cancel; latent Completed-can-be-held fix. 58 tests green.
- **2026-07-17** — Planning interview: vision locked (live self-hosted demo, React dashboard + live event feed, hybrid simulation, failure injection, pipeline-first). Renamed to ArtificeWorks; rewrote docs/Plan; created CLAUDE.md + this file.
