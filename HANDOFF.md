# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-23 (**Epic 11.1 done** — the demo dashboard has a board + timeline; M5 continues)

## Current state

**Settled architecture and its invariants now live in [docs/architecture.md](docs/architecture.md)** —
read that first. The broker detail is in [docs/messaging-topology.md](docs/messaging-topology.md), the
telemetry runbook in [docs/observability.md](docs/observability.md). This section tracks only the
*current* frontier.

Finished epics (detail in each epic file, git history, and architecture.md):

- **Epics 1–3 — synchronous core** (M2): work-order domain + state machine, catalog/work-order REST API, RFC 7807, full test coverage.
- **Epic 4 — messaging** (M3): event contracts + RabbitMQ, worker consumption + dispatch, correlation ids. Direct exchange `artifice.events`.
- **Epic 5 — material picking** (M3): BOM expansion → all-or-nothing reservation (atomic conditional decrement) → `MaterialsReserved`.
- **Epic 6 — production + inspection** (M4): `StockKeepingUnit` lifecycle, per-unit verdicts, bounded rework loop, attempt-scoped idempotency.
- **Epic 7 — shipping + delivery** (M4): `Shipment` aggregate, book + dispatch → `WorkOrderCompleted`, refusal → OnHold, timeline endpoint.
- **Epic 8 — reliability + recovery** (M4): outbox on both publishers, retry ladder, dead letters + replay, `Idempotency-Key`, `xmin`.
- **Epic 9 — observability** (M5): traces (outbox carries `traceparent`), metrics + `/system/stats`, structured logs, health probes, `otel-lgtm`.
- **Epic 10 — simulation engine** (M5): the factory runs itself on a clock. `ArtificeWorks.Simulation` host, pace ladder in `OutboxDispatcher`, `GET/PUT /system/simulation`, `OrderGenerator`, `WorkOrder.Origin`, `WorldResetService`.

**Epic 11 — demo dashboard** (M5, current) — **11.1 done.** New `web/` SPA (Vite + React + TS,
outside the `.sln`) with two views against real data, fetched-not-live: a **board** (orders in
pipeline-stage columns, visitor/robot badged, polls every 4s + manual refresh) and an **order
detail/timeline** (`/work-orders/{id}/timeline` rendered as one column switched on `kind`). Dev
talks to the API through Vite's proxy (`vite.config.ts` → `http://localhost:5181`), root-relative
paths only, **no CORS**. New backend surface: **`GET /work-orders`** — the board read model that
didn't exist (slim `WorkOrderListItemDto`, projected in DB, filterable by repeatable `status` /
`origin`, `limit` clamped to [1,500] default 100, bounded live-world default = in-flight first then
recent-terminal). **Scoped decision:** `WorkOrderListItemDto.Status`/`Origin` serialize as enum
*names* (property-level `JsonStringEnumConverter`) — the rest of the API still emits numeric enums;
a global switch would break every existing test's `ReadFromJsonAsync`, so it's confined to the one
DTO the TS client mirrors. **150 unit tests green; the new list integration tests (`WorkOrderList*
ApiTests`) need Docker to run — not yet executed here.**

## Next up

1. **Recreate the local DB, apply the migration, run the new list integration tests.** (Eight
   migrations were squashed into one `InitialCreate` on 2026-07-23.) `docker compose down -v &&
   docker compose up -d`, then `dotnet ef database update …` (see Notes.md), then `dotnet test
   tests/ArtificeWorks.IntegrationTests` — this is the first run of `WorkOrderListApiTests` /
   `WorkOrderListEmptyApiTests` (Docker was down when 11.1 was written). Then bring the dashboard
   up end-to-end: `dotnet run --project src/ArtificeWorks.Api --launch-profile http` (port 5181),
   the worker + `src/ArtificeWorks.Simulation`, and `cd web && npm install && npm run dev` — the
   board should fill and pace with nobody driving. `PUT /system/simulation` with `FailureRate: 0.4`
   starts the rework loop live.
2. **Epic 11.2 — realtime** ([11.2](docs/Plan/EPIC%2011%20-%20Demo%20dashboard/11.2.md)): API-side
   `artifice.dashboard` relay + SignalR hub that makes the board/detail live and adds the event
   feed (first subscriber for `work-order.faulted`/`completed`). The `web/` client, its typed
   `api/` layer, and the `/hubs` proxy entry are already in place for it. Then
   [11.3](docs/Plan/EPIC%2011%20-%20Demo%20dashboard/11.3.md) affordances and
   [11.4](docs/Plan/EPIC%2011%20-%20Demo%20dashboard/11.4.md) the animated diagram. One story per run;
   working set per story in EPIC_11's implementation plan.
3. **Verify the telemetry against a live stack.** Everything is asserted at the *shape* level, but the
   LogQL/PromQL in the runbook has not been run against real Loki/Prometheus — field naming after OTLP
   ingest is where reality likely differs. ~30 min with the stack up confirms it.

## Open decisions

Settled invariants and their rationale moved to [docs/architecture.md](docs/architecture.md) — nothing
is currently blocked on an undecided question. The few deliberate deferrals still worth remembering:

- **Admin auth gate** — `SetStatus` has no endpoint; `/system/*` (dead letters, stats, simulation) is
  unauthenticated behind that one path prefix until the gate exists.
- **No subscribers yet** on `work-order.faulted` / `work-order.completed` — deliberate; Epic 11's feed.
- **Epic 13 will reopen the reservation key** — widening `material_reservations` to `(WorkOrderId,
  AttemptNumber)` is the honest fix for "rebuilds consume no materials" (see architecture.md).

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update.
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

One line per entry; full detail is in each epic file and the git commit.

- **2026-07-23** — Epic 11.1 done: new `web/` SPA (Vite+React+TS, board + timeline, fetched-not-live, Vite proxy = no CORS) + `GET /work-orders` board read model (slim DTO, projected, `status`/`origin`/`limit` filters, bounded live-world default). Enum names on the list DTO only (property-level converter; global switch would break existing tests' `ReadFromJsonAsync`). 150 unit tests green; list integration tests written but need Docker.
- **2026-07-23** — Epic 11 groomed into 11.1–11.4 (read-only app → realtime → affordances → animated diagram). Key findings: no list/board query exists (11.1 adds `GET /work-orders`); `artifice.events` is a *direct* exchange so the feed binds each `work-order.*` key explicitly (11.2, first subscriber for `faulted`/`completed`). New `web/` SPA outside the solution. README status advanced (10 → Done, 11 → next up).
- **2026-07-23** — Context/token-efficiency pass: created `docs/architecture.md` (settled invariants moved out of Open decisions); trimmed HANDOFF to a rolling window; **squashed 8 migrations into one `InitialCreate`** (no prod data; ~4k→1.9k lines of EF files); added a "don't read generated EF files" note + interview-seed idea (Epic 15) to the plan. Build + 150 unit tests green.
- **2026-07-22** — Epic 10 complete: simulation host, pace ladder, `/system/simulation`, `OrderGenerator`, `WorkOrder.Origin`, `WorldResetService`. 276 tests. `f3d351a` (groom `f39fb05`).
- **2026-07-22** — Epic 9 complete: traces/metrics/logs/health, `otel-lgtm`, `docs/observability.md`. 223 tests. `5ce9935` (groom `3917ee7`).
- **2026-07-22** — Epic 8 complete: outbox on both publishers, retry ladder, dead letters + replay, `Idempotency-Key`, `xmin`. 210 tests.
- **2026-07-21** — Epic 7 complete: `Shipment`, book + dispatch → Completed, refusal → hold, timeline endpoint. 190 tests.
- **2026-07-21** — Epic 6 complete: SKU lifecycle, verdicts, rework loop, attempt-scoped idempotency; fixed unmapped `CompletedBy`. 149 tests.
- **2026-07-21** — Epic 5 complete: BOM + reservation + `CatalogSeeder`, atomic conditional decrement. 90 tests.
- **2026-07-18** — Epic 4 complete: event contracts + RabbitMQ + correlation scopes + `docs/messaging-topology.md`. 76 tests.
- **2026-07-18** — Epic 3 complete: cancellation, RFC 7807 ProblemDetails, integration coverage. 68 tests.
- **2026-07-17** — Planning interview: vision locked, renamed to ArtificeWorks, plan rewritten. Rename `21b1753`, plan `d218f43`.
