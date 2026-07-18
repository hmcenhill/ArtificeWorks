# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line entry to the *Log*; prune anything no longer true. Keep this file under ~80 lines — when a decision becomes permanent, move it to `docs/` (architecture.md or the relevant epic) and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-18 (story 3.3)

## Current state

- Project renamed from EventDrivenOrderProcessingSystem to **ArtificeWorks** (commit `21b1753`): solution, projects, namespaces, `ArtificeWorksDbContext`, DB name, docker containers, CI.
- Plan fully rewritten for the manufacturing pivot (commit `d218f43`): 17 epics / 7 milestones in `docs/Plan/`. Epics 1–2 complete, Epic 3 in progress.
- **Story 3.1 done**: `POST /work-orders/{id}/advance|hold|release`. Domain lifecycle methods return `TransitionResult` (success + reason); controller maps outcomes to 200 / 404 / 409 (Conflict carries the rejection reason as plain text for now — 3.3 makes it ProblemDetails).
- **Story 3.2 done**: `POST /work-orders/{id}/cancel`. Added `Cancelled` terminal status + `WorkOrder.Cancel()`. Terminal states are `Completed` + `Cancelled` (new `_terminalStatuses` set, reused to generalise the Advance/Hold guards). `Fault` is intentionally still cancellable (escape hatch). Cancel releases assigned stock (`_assignedStock.Clear()` — no inventory aggregate yet, so this just detaches units). Also fixed a latent gap where a `Completed` order could be held. No migration (status persists as string). Full reasoning in `docs/Plan/.../3.2.md`.
- **Story 3.3 done**: RFC 7807 ProblemDetails error contract. Every non-2xx now carries a stable `code` extension (`validation_failed`, `work_order_not_found`, `product_not_found`, `product_already_exists`, `terminal_state`, `must_release_first`, `already_held`, `not_held`, `internal_error`). Domain owns the rejection reason: `TransitionResult` carries a `TransitionErrorCode` enum; app responses carry outcome discriminators up; `ApiControllerBase.Problem(...)` + `ProblemCodes` (in `Api/Errors/`) map them to the wire. DTO validation moved to the boundary (`[Range]`/`[Required]` → `validation_failed`, so `qty=0` is a 400 not a domain-thrown 500). Duplicate product create is now **409** (was 400); work-order create with a bad product is **400 product_not_found**. `AddProblemDetails()` + `UseExceptionHandler()` cover 500s; `[ProducesResponseType]` feeds Swagger. All 59 tests green (45 unit + 14 integration). Full reasoning in `docs/Plan/.../3.3.md`.
- Epic 3 remaining: more integration coverage (story 3.4).

## Next up

1. Story 3.4 — integration coverage.

## Open decisions

- `SetStatus` (superuser override) still has no endpoint — deferred to admin auth, as planned.

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update (see Notes.md).
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

- **2026-07-18** — Story 3.3: RFC 7807 ProblemDetails contract with stable `code` per failure; boundary DTO validation; domain rejection codes flow up via `TransitionErrorCode`. Duplicate product create → 409. 59 tests green.
- **2026-07-18** — Story 3.2: cancellation. `Cancelled` terminal status + `cancel` endpoint; terminal guards generalised; Fault kept cancellable; stock released on cancel; latent Completed-can-be-held fix. 58 tests green.
- **2026-07-17** — Planning interview: vision locked (live self-hosted demo, React dashboard + live event feed, hybrid simulation, failure injection, pipeline-first). Renamed to ArtificeWorks; rewrote docs/Plan; created CLAUDE.md + this file.
