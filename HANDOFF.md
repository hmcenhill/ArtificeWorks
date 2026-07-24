# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-23 (**Epic 11 complete** — all four stories done; the dashboard is live, interactive, and has its animated architecture showpiece. M5 wraps here; next is Epic 12)

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
- **Epic 11 — demo dashboard** (M5): new `web/` SPA (Vite + React + TS, outside the `.sln`, Vite-proxied → no CORS). Board + detail/timeline (11.1, `GET /work-orders` read model), SignalR realtime via a read-only `DashboardRelay` → `/hubs/dashboard` (11.2), visitor affordances driving the ordinary endpoints (11.3, added `GET /products`), and the animated architecture diagram at `/architecture` (11.4, pulses off the SignalR stream + strain from `/system/stats`, no backend added). **Two load-bearing frontend gotchas:** the list DTO's enum-**name** converter is *confined to that DTO* (widening it breaks existing numeric-read tests) — which is why `client.ts` decodes the full `WorkOrderDto`'s numeric enums by hand; carriers are mirrored in `web/src/domain/carriers.ts` (no carriers endpoint).

## Next up

1. **Bring the dashboard up end-to-end and watch it live** (needs Docker + a migrated DB; eight
   migrations were squashed into one `InitialCreate` on 2026-07-23, so `docker compose down -v &&
   docker compose up -d`, then `dotnet ef database update …` — see Notes.md). Run the API
   (`dotnet run --project src/ArtificeWorks.Api --launch-profile http`, port 5181), the worker +
   `src/ArtificeWorks.Simulation`, and `cd web && npm run dev`. With generation on, the board should
   fill and **move on its own** and the feed should stream — nobody driving. `PUT /system/simulation`
   with `FailureRate: 0.4` starts the rework loop live and puts `faulted` lines on the feed. **Now
   also open `/architecture`** and watch the diagram pulse off the same stream — a paced order should
   visibly dwell in the broker; raise the failure rate and the workers→broker edge should flash red.
   This is the one part of 11.4 not yet seen against a live stack (build + type-check are green).
2. **Epic 12 — failure injection** (M6, next): the diagram already *shows* a fault and a parked
   message, so 12 becomes mostly giving the visitor the lever (fail an inspection, kill a pick,
   poison a message) and letting the picture do the rest. Groom the epic file into stories.
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

- **2026-07-23** — Epic 11.4 done → **Epic 11 complete**: the animated architecture diagram, the showpiece. Pure frontend, no backend added. Inline SVG topology (API·broker·Workers·Postgres) at `/architecture`; pulses driven entirely by 11.2's SignalR stream (every pulse a real event), event→hop table the only domain knowledge (`web/src/domain/hops.ts`); node strain from a ~5s `/system/stats` poll (`useSystemStats`/`healthFrom`) — outbox backlog, parked-message badge, low stock. One imperative `requestAnimationFrame` loop (no per-frame React), pooled dots, pulse cap, idle-park, clean unmount; reduced-motion + phone handled. `web` type-checks + builds; no backend/tests changed.
- **2026-07-23** — Epic 11.3 done: the dashboard is interactive. Create-order form (`GET /products` + `POST /work-orders` with `Idempotency-Key`, routes to live timeline); decision moments on the detail view (advance/hold/release/book-carrier/verdict/cancel, state-legal, driving the ordinary endpoints, API-authoritative); factory dials panel (round-trips `GET/PUT /system/simulation`, shows source + resolved rung + takes-effect, flagged global). Shared ProblemDetails→sentence mapper. **One backend addition** (the finding the story predicted): `GET /products` list. Two by-hand mirrors: numeric-enum `WorkOrderDto` decoded in a `client.ts` adapter (name converter stays list-DTO-only); carriers in `web/src/domain/carriers.ts`. 151 unit + 5 `ProductApiTests` (incl. new list test) green; web type-checks + builds.
- **2026-07-23** — Epic 11.2 done: the dashboard is live. API-side `DashboardRelay` (read-only, non-competing consumer on auto-delete/TTL'd `artifice.dashboard`, bound to the enumerated `WorkOrderEventTypes.All`, ack-always) → `/hubs/dashboard` SignalR hub → `DashboardEvent`. First subscriber for `faulted`/`completed`. Client: one auto-reconnecting connection (`RealtimeProvider`), board + detail push-driven (`useLiveData` + `useReloadOnStream`, `usePolledData` deleted), live event feed (capped, visitor/robot tagged), header connection status. 151 unit + 3 relay integration tests (relay→client, fan-out, ack-on-failure) green; ran the previously-unrun 11.1 list tests too (green). Docs: messaging-topology relay section + queue table; web README.
- **2026-07-23** — Epic 11.1 done: new `web/` SPA (Vite+React+TS, board + timeline, fetched-not-live, Vite proxy = no CORS) + `GET /work-orders` board read model (slim DTO, projected, `status`/`origin`/`limit` filters, bounded live-world default). Enum names on the list DTO only (property-level converter; global switch would break existing tests' `ReadFromJsonAsync`). 150 unit tests green; list integration tests written but need Docker.
- **2026-07-23** — Epic 11 groomed into 11.1–11.4 (read-only app → realtime → affordances → animated diagram). Key findings: no list/board query exists (11.1 adds `GET /work-orders`); `artifice.events` is a *direct* exchange so the feed binds each `work-order.*` key explicitly (11.2, first subscriber for `faulted`/`completed`). New `web/` SPA outside the solution. README status advanced (10 → Done, 11 → next up).
- **2026-07-23** — Context/token-efficiency pass: created `docs/architecture.md` (settled invariants moved out of Open decisions); trimmed HANDOFF to a rolling window; **squashed 8 migrations into one `InitialCreate`** (no prod data; ~4k→1.9k lines of EF files); added a "don't read generated EF files" note + interview-seed idea (Epic 15) to the plan. Build + 150 unit tests green.
- **2026-07-22** — Epic 10 complete: simulation host, pace ladder, `/system/simulation`, `OrderGenerator`, `WorkOrder.Origin`, `WorldResetService`. 276 tests. `f3d351a` (groom `f39fb05`).
- **2026-07-22** — Epic 9 complete: traces/metrics/logs/health, `otel-lgtm`, `docs/observability.md`. 223 tests. `5ce9935` (groom `3917ee7`).
- **2026-07-18→22** — Epics 3–8 complete (detail in each epic file + git): RFC 7807 + cancellation (3); event contracts + RabbitMQ + correlation (4); BOM + reservation + `CatalogSeeder` (5); SKU lifecycle + verdicts + rework loop (6); `Shipment` + book/dispatch + refusal→hold + timeline (7); outbox + retry ladder + dead letters/replay + `Idempotency-Key` + `xmin` (8).
- **2026-07-17** — Planning interview: vision locked, renamed to ArtificeWorks, plan rewritten. Rename `21b1753`, plan `d218f43`.
