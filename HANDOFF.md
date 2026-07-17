# Handoff — current state

> **Protocol (for Claude):** This is the living hub between conversations. Before the conversation ends, if anything changed: update *Current state*, *Next up*, and *Open decisions*; add a one-line entry to the *Log*; prune anything no longer true. Keep this file under ~80 lines — when a decision becomes permanent, move it to `docs/` (architecture.md or the relevant epic) and drop it from here. Commit this file with the work it describes.

**Last updated:** 2026-07-17

## Current state

- Project renamed from EventDrivenOrderProcessingSystem to **ArtificeWorks** (commit `21b1753`): solution, projects, namespaces, `ArtificeWorksDbContext`, DB name, docker containers, CI. Build + all 36 tests verified green.
- Plan fully rewritten for the manufacturing pivot (commit `d218f43`): 17 epics / 7 milestones in `docs/Plan/`. Epics 1–2 complete, Epic 3 in progress.
- Epic 3 status: work order + product create/get endpoints exist. Lifecycle commands, cancellation, error contract, and test coverage remain (stories 3.1–3.4).

## Next up

1. **Story 3.1** — lifecycle command endpoints (advance/hold/release). Expect it to motivate replacing the domain's `bool` returns with rich results — that refactor is in-scope.
2. Story 3.2 — cancellation (adds `Cancelled` status to domain).
3. Stories 3.3 (ProblemDetails error contract), 3.4 (integration coverage).

## Open decisions

- Does `SetStatus` (superuser override) get an endpoint in 3.1, or wait for admin auth? (Leaning: wait.)
- Stock handling on cancellation (release? return?) — decide in 3.2 and write down the reasoning.

## User to-dos (not Claude's)

- Recreate local DB after rename: `docker compose down -v && docker compose up -d`, then EF update (see Notes.md).
- Rename GitHub repo + local folder to match ArtificeWorks (deferred by choice).
- Push commits when ready.

## Log

- **2026-07-17** — Planning interview: vision locked (live self-hosted demo, React dashboard + live event feed, hybrid simulation, failure injection, pipeline-first). Renamed to ArtificeWorks; rewrote docs/Plan; created CLAUDE.md + this file.
