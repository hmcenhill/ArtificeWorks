# ArtificeWorks

Event-driven manufacturing management system for a fictional automata factory (Hermannsson Artifice Works). Portfolio project targeting a live, self-hosted demo. Full vision, epic roadmap, and sequencing: [docs/Plan/README.md](docs/Plan/README.md).

**Session start: read [HANDOFF.md](HANDOFF.md) for current state and next steps before doing anything else.**

## Working agreement

- Claude writes most of the code; the user directs, reviews, and must always be able to follow what's happening. Explain design decisions and tradeoffs as you go — flag problems, don't silently fix them.
- Work story-by-story from `docs/Plan/`. Stories are groomed just-in-time: epic files exist for everything, story files only for the active epic(s).
- Pipeline-first principle: don't deepen the domain ahead of the async backbone. Keep the system demoable at every milestone.
- Commits go directly to `main` (solo project). Commit at coherent checkpoints with descriptive messages.
- **Before ending a conversation that changed anything: update [HANDOFF.md](HANDOFF.md)** (see the protocol note at its top) and commit it with the work.

## Architecture

Clean architecture, .NET 10:

- `src/ArtificeWorks.Domain` — aggregates, no dependencies. Core: `WorkOrder` state machine (Intake → Scheduled → InProcess → Inspection → Delivery → Completed; OnHold/Fault), `Product`, serialized `StockKeepingUnit`.
- `src/ArtificeWorks.Application` — handlers, DTOs, repository interfaces.
- `src/ArtificeWorks.Infrastructure` — EF Core (Postgres) repositories, migrations; future RabbitMQ implementations.
- `src/ArtificeWorks.Api` — ASP.NET Core controllers, Swagger, health checks.
- `src/ArtificeWorks.Workers` — (empty shell) future event consumers.
- `tests/` — xUnit; integration tests use Testcontainers (**require Docker running**).

## Commands

- Build: `dotnet build` (from root)
- Test: `dotnet test` (unit only: `dotnet test tests/ArtificeWorks.UnitTests`)
- Infra: `docker compose up -d` (Postgres 5432, RabbitMQ 5672/15672, Redis 6379)
- EF migration commands: see [Notes.md](Notes.md)
- CI: GitHub Actions on push/PR to main (build + tests)
