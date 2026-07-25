# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation
> ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line
> entry to the *Log*; prune anything no longer true. **Keep this file a rolling window, ~80 lines
> max.** Detail the *current* epic; collapse finished epics to one line each. When a rule becomes
> permanent, move it to [docs/architecture.md](docs/architecture.md) (the settled invariants) or the
> relevant epic file, and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-24 (**Epic 12.3 done → Epic 12 complete, M6 closed** — the money shot: a contextual chaos panel on the order detail (arms `FailInspection`/`TransientOnce`/`Poison` per-order via `POST /system/chaos`, state-gated for UX, honestly labelled) and the first browser surface over `dead_letters` — a paged/filtered inspector + detail view with in-context Replay (force on second click). Pure frontend; every endpoint already existed. `web` type-checks + builds. **One finding recorded** (below): the SignalR relay streams only the nine business events, so a *park*/*replay* can't be labelled in the feed without a new backend event — left as-is per the story.)

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
- **Epic 12 — failure injection** (M6): visitor-armed chaos → Epic 8's real recovery, on demand. `injected_faults` registry (12.1), the three levers — `FailInspection` (→ rework/Fault loop), `TransientOnce` + `Poison` (→ picking-stage throws over 8.2's ladder / parked queue, 12.2) — arm via rate-limited `POST /system/chaos`, fire-once with the disarm committed outside the rolled-back stage txn. 12.3 is the frontend: a `ChaosPanel` on the order detail (state-gated per `web/src/domain/chaos.ts`, mirroring `ChaosService.IsInjectable`; honestly-labelled sabotage) and the dead-letter inspector at `/dead-letters` (+ `/dead-letters/:id`) — the first UI over `dead_letters`, with a shared `ReplayButton` (handles 202 / `dead_letter_already_replayed`+force / 404). No backend added in 12.3.

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
2. **Watch the whole Epic 12 loop live against a running stack** (needs Docker + the worker + sim).
   The 12.3 UI is built and type-checks, but the end-to-end demo — open an order, arm `Poison`, watch
   it park, open `/dead-letters`, replay, watch the order complete — has not been driven against a
   live floor. This is the epic's demoable finish; ~15 min with the stack up confirms it. (12.1/12.2
   integration tests already passed on real Postgres last session.)
3. **Verify the telemetry against a live stack.** Everything is asserted at the *shape* level, but the
   LogQL/PromQL in the runbook has not been run against real Loki/Prometheus — field naming after OTLP
   ingest is where reality likely differs. ~30 min with the stack up confirms it.

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
- **Epic 13 will reopen the reservation key** — widening `material_reservations` to `(WorkOrderId,
  AttemptNumber)` is the honest fix for "rebuilds consume no materials" (see architecture.md).

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update.
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

One line per entry; full detail is in each epic file and the git commit.

- **2026-07-24** — Epic 12.3 done → **Epic 12 complete, M6 closed**: the money shot is clickable. New `ChaosPanel` (sibling to 11.3's `OrderActions` on the order detail) offers only the stage-legal faults — legality mirrored client-side in new `web/src/domain/chaos.ts` against `ChaosService.IsInjectable`, API's 404/409 the authority on a race — and arms each via the ordinary `POST /system/chaos` (no back door; the lever *is* an endpoint). Styled apart (danger-red section) with honest copy: "kill a worker" is a *simulated* death, each blurb says where to watch it recover (diagram / feed / DLQ). New dead-letter inspector: `DeadLettersView` (`/dead-letters`, paged, waiting/replayed/all filter + a `?workOrderId=` scope chip, over `GET /system/dead-letters`) and `DeadLetterDetailView` (`/dead-letters/:id`, payload pretty-printed + full error, over `GET …/{id}`); shared `ReplayButton` calls `POST …/{id}/replay`, handling 202 / `dead_letter_already_replayed` (→ "Replay again" with `?force=true`) / 404. Loop closes both ways (order → its dead letters, dead letter → its order). Nav entry + two routes. New client fns + wire types (`armChaos`, `fetchDeadLetters`/`fetchDeadLetter`/`replayDeadLetter`; `InjectedFaultKind`, `DeadLetterSummary`/`Detail`/`Page`, `ChaosArmed`, `ReplayResult`), 5 new problem-code sentences. **No backend touched** — every endpoint existed (12.1/12.2/8.3). `web` type-checks + builds (no lint script). **Finding (left as-is per the story):** the SignalR relay binds only the nine business `work-order.*` events, so the feed cannot label a *park*/*replay*/*retry* — those aren't event types. A forced-fail already shows as `rework-required`→`faulted`; an injected poison already bumps the diagram's parked badge off `/system/stats`. Giving the feed a genuine "parked/replayed" line would need a new relay event (an Epic 8 vocabulary change), not a frontend patch — hence `events.ts` unchanged.
- **2026-07-24** — Epic 12.2 done: the two broker faults, and the epic's one hard correctness point proved on real Postgres. `TransientOnce`/`Poison` now fire at the **picking stage** — `MaterialPickingService.PickMaterials` gets an optional `IInjectedFaultRepository?` (nullable, mirroring `InspectionService`, so chaos-less hosts/tests still resolve it) and consults once at the very top via `FireBrokerFaultIfArmed`, *before* the reservation transaction opens, so the `TryConsume` commit survives the throw that follows (disarm-outside-rollback, satisfied structurally). New `InjectedFaultException(kind)` in Application (the service can't reference Workers' `PoisonMessageException`): transient → an ordinary throw the consumer's existing transient branch retries; poison → a new `catch (InjectedFaultException) when (Kind == Poison)` in `RabbitMqConsumerService` that parks it exactly like a real poison. `ChaosService.IsInjectable` gained a cheap guard: broker faults refused once the order is past picking (InProcess/Inspection/Delivery). No new topology — rides 8.2's ladder + parked queue. Docs: messaging-topology "Injected faults ride these same paths" subsection. **Docker was up** — ran the 3 new `InjectedFaultTests` (transient→recover→Completed no park; poison→dead_letters→replay→Completed; neighbour on the same queue untouched) *and* the previously-unrun 12.1 suite (`ChaosApiTests`, `ChaosRateLimitTests`, `ProductionInspectionTests`, `WorldResetTests`) + `DeadLetterTests`/`WorkerConsumerTests`/`RetryLadderTests`/`MaterialPickingTests` — all green. 171 unit tests green (12 new `ChaosTests` theory cases for the broker-fault guard). No migration this story (the registry shape from 12.1 sufficed).
- **2026-07-24** — Epic 12.1 done: the injection backbone + first lever. New `injected_faults` table + `InjectedFaultRow`/`IInjectedFaultRepository`/`InjectedFaultRepository` (Arm idempotent by (order,kind); `TryConsume` a standalone conditional `UPDATE … FOR UPDATE SKIP LOCKED` that fires once and commits outside any stage transaction; `DisarmUnfired`). `ChaosService` (Application) validates the target at the door (terminal/faulted refused, `FailInspection` refused past Delivery) and arms; `POST /system/chaos` (`ChaosController`, per-order, rate-limited — the project's first ASP.NET limiter, fixed window 5/10s by IP; problem codes `chaos_target_not_found`/`_not_injectable`/`_rate_limited`). Consult point: `InspectionService.InspectAttempt` reads an armed `FailInspection` fault as one more verdict input (reason "Failed by injected fault."), routing through Epic 6's rework/Fault loop — no parallel path. `WorldResetService.Sweep` now disarms unfired faults (`WorldResetResult.FaultsDisarmed` added). `AddChaos` DI wired into API + worker; registry TryAdded in `AddWorldReset` for the sim host. Migration `20260725000921_InjectedFaults` (FK→work_orders cascade, target index, `Kind` varchar(30)). 159 unit tests green (8 new: `ChaosTests` + inspection force-fail/fires-once). Integration tests written (`ChaosApiTests`, `ChaosRateLimitTests`, routing in `ProductionInspectionTests`, sweep in `WorldResetTests`) but **not run — Docker unavailable**. `TransientOnce`/`Poison` enum values reserved, unconsumed (12.2).
- **2026-07-24** — Epic 12 groomed into 12.1–12.3 (registry + fail-inspection → the two broker faults → dashboard chaos + DLQ inspector). Key findings from reading the code: the epic is genuinely "wiring existing reliability into the demo" — the recovery paths (retry ladder, parked queue, `dead_letters`, rework loop) all exist; the only new state is an `injected_faults` DB registry (cross-process: API arms, worker fires). Central decisions recorded: `/system/chaos` per-order (opposite blast radius to the global dials, same admin prefix); a fault fires once with the disarm committed *outside* the rolled-back stage transaction (12.2's one subtle correctness point); "kill a worker" is an honestly-labelled simulated death (throw-before-ack), not a real SIGKILL; rate limiter is the project's first. `web` has no DLQ view yet — 12.3 adds it. No code changed; README status advanced (11 → Done, 12 → next up).
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
