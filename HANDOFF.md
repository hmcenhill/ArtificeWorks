# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-31 (**13.1 implemented** — materials per attempt. The reservation key widened to `(WorkOrderId, AttemptNumber)` and the rework loop now re-enters at **picking**, so a rebuild draws real parts. Unit suite green at 179; **integration tests unrun — Docker was not running on this machine**, see *Next up*.)

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

**Current epic — 13, deep domain** (M6, groomed 2026-07-31): [EPIC_13.md](docs/Plan/EPIC%2013%20-%20Deep%20domain%20-%20multi-level%20BOMs%20and%20routings/EPIC_13.md) → **13.1 materials per attempt (done, unverified)** · 13.2 multi-level BOM model + explosion · 13.3 child work orders · 13.4 shared-platform view · 13.5 routings (last, droppable). See the log entries below for the decisions taken at grooming and at 13.1.

## Next up

1. **Verify 13.1 with Docker up** — the only thing between "written" and "done". Nothing in it has
   run against real Postgres, and the `AttemptScopedReservations` migration has never been applied
   (recreate the DB — see Notes.md — since the old unique index on `material_reservations.WorkOrderId`
   must go before a rebuild can pick). New tests to watch: `MaterialPickingTests` (rebuild draws the
   outstanding qty; duplicate rework deliveries reserve once; a short rebuild holds and draws nothing)
   and `ProductionInspectionTests` (the cycle re-enters at picking; an armed `TransientOnce` still
   fires on exactly one pick).
2. **Then 13.2 + 13.3 as one backend run** (the epic's headline), 13.4 alone, 13.5 alone and last.
3. **Bring the whole stack up and watch it live** — still not done, now covering three epics. Docker +
   a migrated DB, then the API (5181), the worker, `src/ArtificeWorks.Simulation`, `cd web && npm run
   dev`. Watch for: the board moving with nobody driving; `/architecture` pulsing off the same stream
   (11.4's one unseen part); Epic 12's loop end to end (arm `Poison` → park → replay → complete); and
   a rebuilt order showing **two** "materials picked" lines on its timeline. ~30 min.
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
  raised then, as a visible decision, rather than pre-emptively.
- **Configure-to-order stays out of Epic 13** (grooming decision) — it remains an Epic 17 candidate.
  Nothing in 13.1–13.5 builds towards a per-order effective BOM.

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update.
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

One line per entry; full detail is in each epic file and the git commit.

- **2026-07-31** — **13.1 implemented: materials per attempt.** `material_reservations` gains `AttemptNumber`; the unique index moves from `WorkOrderId` to `(WorkOrderId, AttemptNumber)` (migration `AttemptScopedReservations`, existing rows backfilled to 1 — the only true reading, and the dropped index is why the backfill can't collide). `IMaterialReservationRepository.GetForWorkOrder` → `GetForAttempt`; `TryReserve` takes the attempt. **The rework loop re-enters at picking**: `ReworkRequiredHandler` now calls `MaterialPickingService` for attempt N+1 with `OutstandingQty`, not `ProductionService` — so production keeps one entry point and a rebuild pays for its parts. `MaterialsReserved` carries `AttemptNumber` (and its `Quantity` is now the pick's, not the order's); `MaterialsReservedHandler` stopped hard-coding 1. **Three things that were not in the story and are worth knowing:** the timeline read model really did assume one reservation per order (`FirstOrDefault`) — fixed here, `TimelineData.Reservation` is now `Reservations` and the DTO emits one `pick` entry per attempt with `attemptNumber` in the detail, plus a chip in `OrderDetailView`; `MaterialReservation.Describe()` names the attempt only from the rebuild on, so the common case reads unchanged while two picks can never read alike; and a `PickOutcome.NothingToPick` was added for a zero demand, which inspection can't publish but which must be a handled no-op rather than a nack onto a ladder it can never climb. 12.2's "a fault fires once" survived the pick becoming per-attempt (`TryConsume` is one-shot) — now asserted rather than assumed. Unit suite 179 green; **integration suite unrun, Docker was down** — see *Next up* for exactly what to watch when it runs. Finding logged in 13.5's notes: `EventContractTests` catches an event shape change by *compiling*, not by asserting, so a field added with a default would round-trip untested.
- **2026-07-31** — Epic 13 groomed into 13.1–13.5 (materials per attempt → multi-level BOM model + explosion → child work orders → the shared-platform view → routings). Read the domain first; the epic's shape follows from what's already there. **Central decisions:** a sub-assembly is a `Product` that *makes* a `Component` (nullable `Component.MakeProductId`) — `bom_lines` untouched, picking untouched for bought parts, "made or bought?" is one nullable column; a child work order is an **ordinary** work order (no new type, no new status — it waits on OnHold, inherits its parent's `Origin`, and is distinguished by its parent link) that on passing inspection is **stocked, not shipped** (putaway credits `components.on_hand`, the parent re-picks through the existing atomic decrement — no second reservation kind, no SKU-to-parent link); the routing walk is **contained inside the production stage**, entered at `materials-reserved` and left at `production-completed`, so inspection/shipping/rework/relay need to know nothing about it; three new "must happen once" keys all fall out of the existing rule (pick per attempt, sub-assembly per parent-attempt-and-component, operation per attempt-and-sequence). Cycle refusal + depth cap flagged as the one place a bug is a *runaway*, not a bad demo — 13.3 spawns work from a BOM walk against a public shared world. CTO explicitly kept out (stays Epic 17). Sequencing: 13.1 alone (it changes Epic 5's unique index), 13.2+13.3 as one run (13.2 alone isn't demoable), 13.4 alone (the check-in point — read-only, and stopping here still closes M6 defensibly), 13.5 alone and last, with work-center capacity marked droppable within it. Two live-world risks recorded: rebuilds now drain the shelves, and one visitor order can put three orders on the board. No code changed; README status already had 12 → Done, 13 → next up.
- **2026-07-24** — **Epic 12 complete (12.1–12.3), M6's failure story closed.** `injected_faults` + `ChaosService` + rate-limited `POST /system/chaos` (12.1; `FailInspection` consulted inside `InspectionService`, routing through Epic 6's rework loop). The two broker faults fire at the **picking stage** via `FireBrokerFaultIfArmed`, before the reservation txn opens, throwing `InjectedFaultException` into the consumer's existing taxonomy (12.2 — disarm-outside-rollback proved on real Postgres; 171 unit tests green). `ChaosPanel` on the order detail + the `/dead-letters` inspector with a shared `ReplayButton`, no backend touched (12.3). **Finding still open:** the relay binds only the nine business `work-order.*` keys, so the feed cannot label a *park*/*replay*/*retry* — those aren't event types; giving them a feed line means a new relay event (an Epic 8 vocabulary change), not a frontend patch.
- **2026-07-23** — **Epic 11 complete (11.1–11.4).** `web/` SPA + `GET /work-orders` board read model (11.1; enum *names* on the list DTO only — a global switch breaks existing numeric-read tests); `DashboardRelay` → `/hubs/dashboard` SignalR, board/detail push-driven, live capped feed (11.2 — first subscriber for `faulted`/`completed`); create form + state-legal decision moments + factory dials, one backend addition `GET /products` (11.3); the animated `/architecture` diagram — pulses off the same stream, strain off `/system/stats`, one `requestAnimationFrame` loop (11.4). Also that week: `docs/architecture.md` created and 8 migrations squashed into one `InitialCreate`.
- **2026-07-22** — Epic 10 complete: simulation host, pace ladder, `/system/simulation`, `OrderGenerator`, `WorkOrder.Origin`, `WorldResetService`. 276 tests. `f3d351a` (groom `f39fb05`).
- **2026-07-22** — Epic 9 complete: traces/metrics/logs/health, `otel-lgtm`, `docs/observability.md`. 223 tests. `5ce9935` (groom `3917ee7`).
- **2026-07-18→22** — Epics 3–8 complete (detail in each epic file + git): RFC 7807 + cancellation (3); event contracts + RabbitMQ + correlation (4); BOM + reservation + `CatalogSeeder` (5); SKU lifecycle + verdicts + rework loop (6); `Shipment` + book/dispatch + refusal→hold + timeline (7); outbox + retry ladder + dead letters/replay + `Idempotency-Key` + `xmin` (8).
- **2026-07-17** — Planning interview: vision locked, renamed to ArtificeWorks, plan rewritten. Rename `21b1753`, plan `d218f43`.
