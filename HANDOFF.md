# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-31 (**13.2 implemented, and 13.1 verified.** A component can now be *made* — one nullable `Component.MakeProductId` turns the flat BOM into a tree — with a recursive `BomExplosion`, a three-level seeded catalog, and `GET /products/{id}/bom`. **Docker was up: the whole suite ran, 192 unit + 157 integration green**, which also closed out 13.1's unverified integration tests. One 13.1 test defect found and fixed — see the log.)

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
- **Epic 11 — demo dashboard** (M5): the `web/` SPA (Vite + React + TS, outside the `.sln`, Vite-proxied → no CORS) — board + timeline, SignalR realtime via a read-only `DashboardRelay` → `/hubs/dashboard`, visitor affordances driving the ordinary endpoints, and the animated `/architecture` diagram. **Two load-bearing frontend gotchas:** the list DTO's enum-**name** converter is *confined to that DTO* (widening it breaks existing numeric-read tests) — which is why `client.ts` decodes the full `WorkOrderDto`'s numeric enums by hand; carriers are mirrored in `web/src/domain/carriers.ts` (no carriers endpoint).
- **Epic 12 — failure injection** (M6): visitor-armed chaos → Epic 8's real recovery, on demand. `injected_faults` registry + rate-limited `POST /system/chaos`; three levers (`FailInspection` → rework/Fault loop; `TransientOnce`/`Poison` → picking-stage throws over 8.2's ladder / parked queue), each firing once with the disarm committed outside the rolled-back stage txn. Frontend: the order detail's `ChaosPanel` and the dead-letter inspector at `/dead-letters`.

**Current epic — 13, deep domain** (M6, groomed 2026-07-31): [EPIC_13.md](docs/Plan/EPIC%2013%20-%20Deep%20domain%20-%20multi-level%20BOMs%20and%20routings/EPIC_13.md) → **13.1 materials per attempt (done, verified)** · **13.2 multi-level BOM model + explosion (done, verified)** · 13.3 child work orders · 13.4 shared-platform view · 13.5 routings (last, droppable). See the log entries below for the decisions taken at grooming, 13.1 and 13.2.

## Next up

1. **13.3 — child work orders**, the epic's headline. Grooming batched 13.2+13.3 as one run; 13.2 was
   taken on its own instead, which paid off as intended — the model, the tree and the deeper catalog
   are in and green, so 13.3 is about *one* new decision rather than a new model and a new decision
   at once: a missing made component spawns a child order, which is stocked rather than shipped,
   and the parent waits OnHold until the child's `work-order.completed` releases it. Take the grooming
   plan's `Explore` sweep first ("how does an order reach `Completed`, and what would have to be true
   for a *second* order to be created from inside a worker handler?"). Note 13.2 left the spawn
   *unexercised on purpose*: every made component carries seeded stock, so nothing is short yet — the
   first thing 13.3 needs is a way to make one short.
2. Then 13.4 alone (the check-in point), 13.5 alone and last.
3. **Bring the whole stack up and watch it live** — still not done, now covering three epics. Docker +
   a migrated DB, then the API (5181), the worker, `src/ArtificeWorks.Simulation`, `cd web && npm run
   dev`. Watch for: the board moving with nobody driving; `/architecture` pulsing off the same stream
   (11.4's one unseen part); Epic 12's loop end to end (arm `Poison` → park → replay → complete); a
   rebuilt order showing **two** "materials picked" lines on its timeline; and the create form still
   offering exactly **three** templates now the catalog holds six products. ~30 min.
4. **Verify the telemetry against a live stack.** Asserted at the *shape* level only — the runbook's
   LogQL/PromQL has never run against real Loki/Prometheus, and field naming after OTLP ingest is
   where reality likely differs. ~30 min with the stack up.

## Open decisions

Settled invariants and their rationale moved to [docs/architecture.md](docs/architecture.md) — nothing
is currently blocked on an undecided question. The few deliberate deferrals still worth remembering:

- **Admin auth gate** — `SetStatus` has no endpoint; `/system/*` (dead letters, stats, simulation,
  chaos) is unauthenticated behind that one path prefix until the gate exists. `/system/chaos` has a
  rate limiter (fixed window, 5 per 10s, keyed by caller IP) as an interim guardrail.
- **`work-order.faulted` / `work-order.completed`** now have exactly one subscriber, the 11.2
  dashboard relay (no *pipeline* consumer, still by design).
- **Dashboard relay is single-instance** — one fixed-name `artifice.dashboard` queue. A scaled API
  would give each instance its own queue + a SignalR backplane (an Epic 15 concern).
- **The shelves now drain faster, and the seed data was deliberately not touched** (13.1). Every
  rebuild is a real draw; the binding constraint is the Delver line (`CMP-SENS-SEISMIC` 120 at 2/unit
  → 60 units, `CMP-LOCO-LEG` 90 at 1/unit). 10.4's sweep still restocks to `seed_on_hand`, so the
  world heals. **If the live demo starves, the fix is `CatalogSeeder`'s numbers, not the design** —
  raised then, as a visible decision, rather than pre-emptively. 13.2 did *not* change the drain: the
  eight new components sit under sub-assemblies nothing consumes until 13.3, and the two made
  components keep the on-hand they always had.
- **Sub-assemblies are ordinary products, so `GET /products` now returns six rows.** They carry
  `isSubAssembly` and the create form filters on it, which is the only frontend change in 13.2. The
  alternative — hiding them from the catalog — would have made 13.4 unable to show what a shared
  platform is built from.
- **Configure-to-order stays out of Epic 13** (grooming decision) — it remains an Epic 17 candidate.
  Nothing in 13.1–13.5 builds towards a per-order effective BOM.

## User to-dos (not Claude's)

- Recreate local DB: `docker compose down -v && docker compose up -d`, then EF update. Two migrations
  are pending on any existing local DB — `AttemptScopedReservations` (13.1; the old unique index on
  `material_reservations.WorkOrderId` must go before a rebuild can pick) and `ManufacturedComponents`
  (13.2). Both ran clean against fresh Testcontainers Postgres.
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

One line per entry; full detail is in each epic file and the git commit.

- **2026-07-31** — **13.2 implemented: a component the factory makes.** `components.make_product_id`
  (nullable FK → `products.ItemId`, migration `ManufacturedComponents`, `Restrict` on delete so
  removing a sub-assembly product cannot silently take the component and every `bom_lines` row that
  calls for it) is the entire multi-level BOM — `bom_lines` untouched, picking untouched.
  `BomExplosion` (Domain, pure, catalog passed in) returns both shapes: the tree and the aggregated
  bought-leaf demand, `MaxDepth` 5, refusing a cycle, a too-deep chain and a made component whose
  maker is missing (that last one is an error, not a leaf — treating it as bought would understate
  demand silently). **The cycle guard tracks the *path*, not a visited set** — that is what lets a
  diamond be expanded on both branches and aggregated into one leaf row, and there is a test for
  exactly that regression. `Product.ComputeDemand` is unchanged and now has a test saying so.
  **Catalog is three levels deep**: `SUBASM-CTRL-STACK` → `CMP-CTRL-STACK`, and `SUBASM-CORE` →
  `CMP-CORE-AETHER` via `CMP-CASING-CORE` ← `SUBASM-CORE-CASING`, so the recursion runs against real
  seed data every time. **The shared-platform claim is asserted at both levels**: 70% of the flat BOM
  (unchanged) *and* 12 of 15 exploded leaves = 80%, plus two shared sub-assemblies. **Three calls
  that weren't in the story:** `SeededProductIds` still means *saleable* lines only, because 10.3's
  `OrderGenerator` picks from it (new `SubAssemblyProductIds` / `MadeComponents` alongside it); the
  API took the sub-resource `GET /products/{id}/bom?qty=N` (409 `bom_not_explodable` — the request
  was fine, the data isn't) rather than fattening `ProductDto`; and `ProductSummaryDto` gained
  `IsSubAssembly` so the create form still offers three templates from a six-product catalog.
- **2026-07-31** — **13.1 verified against real Postgres**, and one test defect found in it.
  `Simultaneous_duplicate_rework_deliveries_reserve_once_for_that_attempt` asserted the losing
  delivery published no `MaterialsReserved` — but `RecordingEventPublisher` is an in-memory queue
  that knows nothing about rollback, so it keeps an announcement the transaction discarded. **The
  production code is correct**; the assertion reached for a property the harness cannot express.
  Re-pointed at the state-history note, which *is* written by the reservation transaction and rolls
  back with it. **Finding for Epic 14:** that publisher silently makes "was this published?"
  untestable under rollback; anything needing it must go through `OutboxTests`.
- **2026-07-31** — **13.1 implemented: materials per attempt.** `material_reservations` gains
  `AttemptNumber` and the unique index moves to `(WorkOrderId, AttemptNumber)` (migration
  `AttemptScopedReservations`). **The rework loop re-enters at picking** — `ReworkRequiredHandler`
  calls `MaterialPickingService` for attempt N+1 with `OutstandingQty`, so production keeps one entry
  point and a rebuild pays for its parts. The timeline emits one `pick` entry per attempt.
  Finding logged in 13.5's notes: `EventContractTests` catches an event shape change by *compiling*,
  not by asserting, so a field added with a default would round-trip untested.
- **2026-07-31** — Epic 13 groomed into 13.1–13.5. **The eight decisions taken there are written up
  in full in [EPIC_13.md](docs/Plan/EPIC%2013%20-%20Deep%20domain%20-%20multi-level%20BOMs%20and%20routings/EPIC_13.md)
  ("Decisions taken at grooming") — read them before 13.3, since three of them are about the child
  work order.** The one that still binds hardest: a child order is an *ordinary* work order that
  hands back **stock, not units**, which is what keeps the epic small. CTO explicitly kept out
  (stays Epic 17). No code changed.
- **2026-07-24** — **Epic 12 complete (12.1–12.3), M6's failure story closed.** `injected_faults` + `ChaosService` + rate-limited `POST /system/chaos` (12.1; `FailInspection` consulted inside `InspectionService`, routing through Epic 6's rework loop). The two broker faults fire at the **picking stage** via `FireBrokerFaultIfArmed`, before the reservation txn opens, throwing `InjectedFaultException` into the consumer's existing taxonomy (12.2 — disarm-outside-rollback proved on real Postgres; 171 unit tests green). `ChaosPanel` on the order detail + the `/dead-letters` inspector with a shared `ReplayButton`, no backend touched (12.3). **Finding still open:** the relay binds only the nine business `work-order.*` keys, so the feed cannot label a *park*/*replay*/*retry* — those aren't event types; giving them a feed line means a new relay event (an Epic 8 vocabulary change), not a frontend patch.
- **2026-07-23** — **Epic 11 complete (11.1–11.4).** `web/` SPA + `GET /work-orders` board read model (11.1; enum *names* on the list DTO only — a global switch breaks existing numeric-read tests); `DashboardRelay` → `/hubs/dashboard` SignalR, board/detail push-driven, live capped feed (11.2 — first subscriber for `faulted`/`completed`); create form + state-legal decision moments + factory dials, one backend addition `GET /products` (11.3); the animated `/architecture` diagram — pulses off the same stream, strain off `/system/stats`, one `requestAnimationFrame` loop (11.4). Also that week: `docs/architecture.md` created and 8 migrations squashed into one `InitialCreate`.
- **2026-07-22** — Epic 10 complete: simulation host, pace ladder, `/system/simulation`, `OrderGenerator`, `WorkOrder.Origin`, `WorldResetService`. 276 tests. `f3d351a` (groom `f39fb05`).
- **2026-07-22** — Epic 9 complete: traces/metrics/logs/health, `otel-lgtm`, `docs/observability.md`. 223 tests. `5ce9935` (groom `3917ee7`).
- **2026-07-18→22** — Epics 3–8 complete (detail in each epic file + git): RFC 7807 + cancellation (3); event contracts + RabbitMQ + correlation (4); BOM + reservation + `CatalogSeeder` (5); SKU lifecycle + verdicts + rework loop (6); `Shipment` + book/dispatch + refusal→hold + timeline (7); outbox + retry ladder + dead letters/replay + `Idempotency-Key` + `xmin` (8).
- **2026-07-17** — Planning interview: vision locked, renamed to ArtificeWorks, plan rewritten. Rename `21b1753`, plan `d218f43`.
