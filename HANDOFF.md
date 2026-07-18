# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line entry to the *Log*; prune anything no longer true. Keep this file under ~80 lines — when a decision becomes permanent, move it to `docs/` (architecture.md or the relevant epic) and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-18 (story 4.1)

## Current state

- Project renamed from EventDrivenOrderProcessingSystem to **ArtificeWorks** (commit `21b1753`): solution, projects, namespaces, `ArtificeWorksDbContext`, DB name, docker containers, CI.
- Plan fully rewritten for the manufacturing pivot (commit `d218f43`): 17 epics / 7 milestones in `docs/Plan/`. **Epics 1–3 complete** — synchronous core done: work-order domain + state machine, catalog/work-order REST API, RFC 7807 ProblemDetails contract, full unit + integration coverage. Per-story detail in `docs/Plan/` and the Log below.
- **Epic 4 (messaging) in progress** — the async pivot.
- **Epic 4 stories reviewed** (start of epic): 4.1/4.2/4.3 confirmed sound; added a locked **Decisions** block to 4.1 and clarified 4.1↔4.3 correlation split + 4.1↔4.2 testing split. See the epic folder.
- **Story 4.1 done** — event contracts + publisher (first async plumbing). `Application/Messaging/`: `IntegrationEvent` base (stable `EventType` = routing key, `SchemaVersion`), `EventEnvelope<T>`, `IEventPublisher`, `ICorrelationContext` + mutable `CorrelationContext`, and `WorkOrderCreated`/`WorkOrderScheduled` payloads. `Infrastructure/Messaging/`: `RabbitMqConfiguration` (moved from Api, gained `ExchangeName`), lazy singleton `RabbitMqConnection` (declares the durable **direct** exchange `artifice.events` on first connect), scoped `RabbitMqEventPublisher` (System.Text.Json web-defaults, persistent delivery, AMQP `type`/`message_id`/`correlation_id`), and `AddRabbitMqMessaging`. Api: `CorrelationMiddleware` (honours/echoes `X-Correlation-ID`), messaging registered. Handler publishes **after commit**, **best-effort** (broker outage logs + drops, never fails the request): `WorkOrderCreated` on create, `WorkOrderScheduled` when an advance lands Intake→Scheduled. Lib = official `RabbitMQ.Client` 7.x (async `IChannel`). API integration tests use a `NoOpEventPublisher` (broker-free). **72 tests green (49 unit incl. 4 new contract tests + 23 integration).** Verified end-to-end against a live broker: advanced a work order, pulled the real `work-order.scheduled` message off a bound queue — envelope + correlation id (from the request header) intact.

## Next up

1. **Story 4.2** — Workers becomes a Generic Host, consumes `artifice.events`, dispatches to typed handlers (nack `requeue:false`, no DLQ yet), + Testcontainers RabbitMQ E2E test. Add Workers to CI. Notes locked in `docs/Plan/.../4.2.md`.
2. Story 4.3 — harden correlation into log scopes on both services; write the topology doc.

## Open decisions

- `SetStatus` (superuser override) still has no endpoint — deferred to admin auth, as planned.
- **No optimistic concurrency on `WorkOrder`** (no rowversion / `xmin`). Concurrent *same-order* advances would duplicate history / lose updates silently — so 3.4 tests concurrent *create* only. Deferred; the Epic 4 async pivot moves transitions onto a queue and will revisit aggregate concurrency. (Detailed in `docs/Plan/.../3.4.md`.)
- **Publishing is best-effort, at-most-once** (4.1). Publish-after-commit with a swallow-and-log on broker failure: a state change can persist while its event is dropped (the classic dual-write gap). Chosen to keep the synchronous core resilient to broker outages. Epic 8's transactional outbox closes it.

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update (see Notes.md).
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

- **2026-07-18** — Story 4.1: event contracts + RabbitMQ publisher (first async plumbing). `EventEnvelope<T>` + `WorkOrderCreated`/`WorkOrderScheduled` in Application; `RabbitMqConnection`/`RabbitMqEventPublisher` + direct exchange `artifice.events` in Infrastructure; correlation middleware in Api; handler publishes best-effort after commit. Verified end-to-end against a live broker. 72 tests green.
- **2026-07-18** — Story 3.4: integration coverage closed (Epic 3 complete). `ProductApiTests` + work-order additions (history-404, hold/advance rejections, full-lifecycle walk, concurrent-create smoke check); shared `ReadProblemCodeAsync`. 68 tests green. Flagged missing aggregate optimistic-concurrency (concurrent advance untested by design).
- **2026-07-18** — Story 3.3: RFC 7807 ProblemDetails contract with stable `code` per failure; boundary DTO validation; domain rejection codes flow up via `TransitionErrorCode`. Duplicate product create → 409. 59 tests green.
- **2026-07-18** — Story 3.2: cancellation. `Cancelled` terminal status + `cancel` endpoint; terminal guards generalised; Fault kept cancellable; stock released on cancel; latent Completed-can-be-held fix. 58 tests green.
- **2026-07-17** — Planning interview: vision locked (live self-hosted demo, React dashboard + live event feed, hybrid simulation, failure injection, pipeline-first). Renamed to ArtificeWorks; rewrote docs/Plan; created CLAUDE.md + this file.
