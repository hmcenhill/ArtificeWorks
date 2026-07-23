# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-23 (**Epic 11.2 done** — the dashboard is live over SignalR; M5 continues)

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

**Epic 11 — demo dashboard** (M5, current) — **11.1 + 11.2 done.** New `web/` SPA (Vite + React +
TS, outside the `.sln`): a **board** (orders in pipeline-stage columns, visitor/robot badged), an
**order detail/timeline**, and now a **live event feed**. Dev talks to the API through Vite's proxy
(`vite.config.ts` → `http://localhost:5181`), root-relative paths only, **no CORS**; `/hubs`
proxied with `ws:true`.

- **11.1 backend:** `GET /work-orders` board read model (slim `WorkOrderListItemDto`, projected,
  repeatable `status`/`origin` filters, `limit` clamped [1,500] default 100, bounded live-world
  default). `WorkOrderListItemDto.Status`/`Origin` serialize as enum *names* (property-level
  converter) — confined to the one DTO the TS client mirrors so existing tests' numeric-enum
  `ReadFromJsonAsync` stays green.
- **11.2 realtime:** the API grows a **read-only, non-competing consumer** — `DashboardRelay`, a
  hosted service on its own `artifice.dashboard` queue (auto-delete + `x-message-ttl`), bound to
  `WorkOrderEventTypes.All` (the single enumerated list of published keys; a drift unit test keeps
  it honest). It relays each event to browsers over the **`/hubs/dashboard` SignalR hub** as a slim
  `DashboardEvent` (metadata + `workOrderId`). **Ack-always**, never retries/parks (a dropped
  broadcast is a screen frame, not a unit of work); it's the **first subscriber for `faulted` /
  `completed`**. First-connect is retried so a broker blip doesn't kill the API. The broadcast seam
  is `IDashboardBroadcaster` (testable). Client: `RealtimeProvider` owns one auto-reconnecting
  connection; board + detail reload on relevant events (debounced) + reconcile on reconnect — no
  interval poll; feed streams newest-first, capped, visitor/robot tagged; header shows connection
  state. **`usePolledData` deleted** (replaced by `useLiveData` + `useReloadOnStream`).
- **Tests green:** 151 unit; `DashboardRelayTests` (3, Testcontainers broker: relay→SignalR client,
  fan-out doesn't steal from the worker queue, broadcast-failure still acks) + the previously-unrun
  `WorkOrderList*ApiTests` (5) all pass with Docker up.

## Next up

1. **Bring the dashboard up end-to-end and watch it live** (needs Docker + a migrated DB; eight
   migrations were squashed into one `InitialCreate` on 2026-07-23, so `docker compose down -v &&
   docker compose up -d`, then `dotnet ef database update …` — see Notes.md). Run the API
   (`dotnet run --project src/ArtificeWorks.Api --launch-profile http`, port 5181), the worker +
   `src/ArtificeWorks.Simulation`, and `cd web && npm run dev`. With generation on, the board should
   fill and **move on its own** and the feed should stream — nobody driving. `PUT /system/simulation`
   with `FailureRate: 0.4` starts the rework loop live and puts `faulted` lines on the feed.
2. **Epic 11.3 — visitor affordances**
   ([11.3](docs/Plan/EPIC%2011%20-%20Demo%20dashboard/11.3.md)): create an order from a template,
   make the hybrid-model decisions (approve schedule, choose spec), turn 10.2's dials — all through
   the *ordinary* endpoints (no dashboard back door). Then
   [11.4](docs/Plan/EPIC%2011%20-%20Demo%20dashboard/11.4.md) the animated diagram, which is
   presentation over **this** story's SignalR stream. One story per run; working set per story in
   EPIC_11's implementation plan.
3. **Verify the telemetry against a live stack.** Everything is asserted at the *shape* level, but the
   LogQL/PromQL in the runbook has not been run against real Loki/Prometheus — field naming after OTLP
   ingest is where reality likely differs. ~30 min with the stack up confirms it.

## Open decisions

Settled invariants and their rationale moved to [docs/architecture.md](docs/architecture.md) — nothing
is currently blocked on an undecided question. The few deliberate deferrals still worth remembering:

- **Admin auth gate** — `SetStatus` has no endpoint; `/system/*` (dead letters, stats, simulation) is
  unauthenticated behind that one path prefix until the gate exists.
- **`work-order.faulted` / `work-order.completed`** now have exactly one subscriber, the 11.2
  dashboard relay (no *pipeline* consumer, still by design).
- **Dashboard relay is single-instance** — one fixed-name `artifice.dashboard` queue. A scaled API
  would give each instance its own queue + a SignalR backplane (an Epic 15 concern).
- **Epic 13 will reopen the reservation key** — widening `material_reservations` to `(WorkOrderId,
  AttemptNumber)` is the honest fix for "rebuilds consume no materials" (see architecture.md).

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update.
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

One line per entry; full detail is in each epic file and the git commit.

- **2026-07-23** — Epic 11.2 done: the dashboard is live. API-side `DashboardRelay` (read-only, non-competing consumer on auto-delete/TTL'd `artifice.dashboard`, bound to the enumerated `WorkOrderEventTypes.All`, ack-always) → `/hubs/dashboard` SignalR hub → `DashboardEvent`. First subscriber for `faulted`/`completed`. Client: one auto-reconnecting connection (`RealtimeProvider`), board + detail push-driven (`useLiveData` + `useReloadOnStream`, `usePolledData` deleted), live event feed (capped, visitor/robot tagged), header connection status. 151 unit + 3 relay integration tests (relay→client, fan-out, ack-on-failure) green; ran the previously-unrun 11.1 list tests too (green). Docs: messaging-topology relay section + queue table; web README.
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
