# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-23 (**Epic 11.3 done** — the dashboard is interactive; only 11.4 left in M5's Epic 11)

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

**Epic 11 — demo dashboard** (M5, current) — **11.1 + 11.2 + 11.3 done; only 11.4 left.** New
`web/` SPA (Vite + React + TS, outside the `.sln`): a **board** (orders in pipeline-stage columns,
visitor/robot badged), an **order detail/timeline** with **decision-moment actions**, a **live
event feed**, a **create-order form**, and a **factory dials panel**. Dev talks to the API through
Vite's proxy (`vite.config.ts` → `http://localhost:5181`), root-relative paths only, **no CORS**;
`/hubs` proxied with `ws:true`.

- **11.1 (settled; detail in the epic file):** `GET /work-orders` board read model — slim
  `WorkOrderListItemDto`, repeatable `status`/`origin` filters, `limit` [1,500]/default 100, bounded
  live-world default. Its `Status`/`Origin` serialize as enum *names* via a **property-level**
  converter, **confined to this one DTO** so existing tests' numeric reads stay green (see 11.3's
  adapter note — this confinement is why that adapter exists).
- **11.2 (settled; detail in the epic file):** read-only, non-competing `DashboardRelay` on its own
  auto-delete/TTL `artifice.dashboard` queue (bound to `WorkOrderEventTypes.All`, ack-always, first
  subscriber for `faulted`/`completed`) → `/hubs/dashboard` SignalR hub → slim `DashboardEvent`.
  Client: `RealtimeProvider` owns one auto-reconnecting connection; board + detail push-driven
  (`useLiveData` + `useReloadOnStream`); capped live feed; header connection status.
- **11.3 visitor affordances:** almost entirely frontend, driving the *ordinary* endpoints (no
  dashboard back door). **Create** (`/create`) reads `GET /products` and `POST /work-orders` with an
  `Idempotency-Key`, then routes to the new order's live timeline. **Decision moments** on the order
  detail (state-legal only, API is authority): advance, hold/release, book carrier, record verdict,
  cancel — attributed to `visitor`. **Dials** (`/controls`) round-trip `GET/PUT /system/simulation`
  (PUT is whole-object), showing source, resolved rung and takes-effect, and flagged **global**. A
  shared code-keyed ProblemDetails→sentence mapper (`web/src/api/problems.ts`). The **one backend
  addition** was the finding the story predicted: no `GET /products` list existed → added
  (`IProductRepository.List` → `ProductRepository` → `ProductHandler.ListProducts` → controller +
  slim `ProductSummaryDto`). Two contained bits of by-hand mirroring: the full `WorkOrderDto`'s
  **numeric** enums are decoded to names in one `client.ts` adapter (name converter stays confined
  to the list DTO — don't widen it, it keeps existing tests green); carriers mirror
  `ShippingConfiguration.DefaultCarriers` in `web/src/domain/carriers.ts` (no carriers endpoint).
- **Tests green:** 151 unit; `DashboardRelayTests` (3) + `WorkOrderList*ApiTests` (5) +
  `ProductApiTests` (5, now incl. the new `ListProducts_ReturnsCreatedProducts`) pass with Docker
  up. `web` type-checks + builds. 11.3 added no new backend tests beyond the products-list one; the
  decision-moment endpoints it drives were already covered by Epics 5–10.

## Next up

1. **Bring the dashboard up end-to-end and watch it live** (needs Docker + a migrated DB; eight
   migrations were squashed into one `InitialCreate` on 2026-07-23, so `docker compose down -v &&
   docker compose up -d`, then `dotnet ef database update …` — see Notes.md). Run the API
   (`dotnet run --project src/ArtificeWorks.Api --launch-profile http`, port 5181), the worker +
   `src/ArtificeWorks.Simulation`, and `cd web && npm run dev`. With generation on, the board should
   fill and **move on its own** and the feed should stream — nobody driving. `PUT /system/simulation`
   with `FailureRate: 0.4` starts the rework loop live and puts `faulted` lines on the feed.
2. **Epic 11.4 — the showpiece: animated architecture diagram**
   ([11.4](docs/Plan/EPIC%2011%20-%20Demo%20dashboard/11.4.md)): presentation over 11.2's SignalR
   stream (components pulse on real events) with strain colour from `/system/stats`. The last story
   in Epic 11. Working set in EPIC_11's implementation plan: `docs/architecture.md` (topology to
   draw), `SystemStatsController.cs` / `SystemStatsDto.cs`, and 11.2's SignalR client.
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

- **2026-07-23** — Epic 11.3 done: the dashboard is interactive. Create-order form (`GET /products` + `POST /work-orders` with `Idempotency-Key`, routes to live timeline); decision moments on the detail view (advance/hold/release/book-carrier/verdict/cancel, state-legal, driving the ordinary endpoints, API-authoritative); factory dials panel (round-trips `GET/PUT /system/simulation`, shows source + resolved rung + takes-effect, flagged global). Shared ProblemDetails→sentence mapper. **One backend addition** (the finding the story predicted): `GET /products` list. Two by-hand mirrors: numeric-enum `WorkOrderDto` decoded in a `client.ts` adapter (name converter stays list-DTO-only); carriers in `web/src/domain/carriers.ts`. 151 unit + 5 `ProductApiTests` (incl. new list test) green; web type-checks + builds.
- **2026-07-23** — Epic 11.2 done: the dashboard is live. API-side `DashboardRelay` (read-only, non-competing consumer on auto-delete/TTL'd `artifice.dashboard`, bound to the enumerated `WorkOrderEventTypes.All`, ack-always) → `/hubs/dashboard` SignalR hub → `DashboardEvent`. First subscriber for `faulted`/`completed`. Client: one auto-reconnecting connection (`RealtimeProvider`), board + detail push-driven (`useLiveData` + `useReloadOnStream`, `usePolledData` deleted), live event feed (capped, visitor/robot tagged), header connection status. 151 unit + 3 relay integration tests (relay→client, fan-out, ack-on-failure) green; ran the previously-unrun 11.1 list tests too (green). Docs: messaging-topology relay section + queue table; web README.
- **2026-07-23** — Epic 11.1 done: new `web/` SPA (Vite+React+TS, board + timeline, fetched-not-live, Vite proxy = no CORS) + `GET /work-orders` board read model (slim DTO, projected, `status`/`origin`/`limit` filters, bounded live-world default). Enum names on the list DTO only (property-level converter; global switch would break existing tests' `ReadFromJsonAsync`). 150 unit tests green; list integration tests written but need Docker.
- **2026-07-23** — Epic 11 groomed into 11.1–11.4 (read-only app → realtime → affordances → animated diagram). Key findings: no list/board query exists (11.1 adds `GET /work-orders`); `artifice.events` is a *direct* exchange so the feed binds each `work-order.*` key explicitly (11.2, first subscriber for `faulted`/`completed`). New `web/` SPA outside the solution. README status advanced (10 → Done, 11 → next up).
- **2026-07-23** — Context/token-efficiency pass: created `docs/architecture.md` (settled invariants moved out of Open decisions); trimmed HANDOFF to a rolling window; **squashed 8 migrations into one `InitialCreate`** (no prod data; ~4k→1.9k lines of EF files); added a "don't read generated EF files" note + interview-seed idea (Epic 15) to the plan. Build + 150 unit tests green.
- **2026-07-22** — Epic 10 complete: simulation host, pace ladder, `/system/simulation`, `OrderGenerator`, `WorkOrder.Origin`, `WorldResetService`. 276 tests. `f3d351a` (groom `f39fb05`).
- **2026-07-22** — Epic 9 complete: traces/metrics/logs/health, `otel-lgtm`, `docs/observability.md`. 223 tests. `5ce9935` (groom `3917ee7`).
- **2026-07-18→22** — Epics 3–8 complete (detail in each epic file + git): RFC 7807 + cancellation (3); event contracts + RabbitMQ + correlation (4); BOM + reservation + `CatalogSeeder` (5); SKU lifecycle + verdicts + rework loop (6); `Shipment` + book/dispatch + refusal→hold + timeline (7); outbox + retry ladder + dead letters/replay + `Idempotency-Key` + `xmin` (8).
- **2026-07-17** — Planning interview: vision locked, renamed to ArtificeWorks, plan rewritten. Rename `21b1753`, plan `d218f43`.
