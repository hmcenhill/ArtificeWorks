# ArtificeWorks — Project Plan

## The company

Hermannsson Artifice Works builds automata for the work humans shouldn't have to do alone. Founded on a single shared platform — a common chassis, power core, and control architecture — the company produces three configurable product lines from one manufacturing backbone: the **Custodian**, a service automaton for logistics and facility work; the **Delver**, a rugged mining unit built for extraction and hazardous terrain; and the **Courier**, a wheeled delivery automaton for last-mile transport. Every unit ships configure-to-order, its tool-hands, locomotion, and sensor suite swapped from a shared parts catalog to match the buyer's trade — a mine-spec Delver shares 70% of its bill of materials with a hospitality Custodian. It's old-world craftsmanship married to modern platform engineering: Hermannsson doesn't build robots so much as it builds bodies, purpose-fit for whatever work needs doing.

## The system

ArtificeWorks is the event-driven software that runs the factory: work order scheduling, BOM-driven material picking, production and inspection workflows, and shipment scheduling. Everything is virtual — no real payments, suppliers, or logistics.

**End state:** a live demo self-hosted on a home server and embedded in a personal website. Visitors create work orders and make key decisions; simulated workers handle picking, production, and inspection. A purpose-built React dashboard shows the factory *and* the machinery underneath — a live event feed and an animated architecture diagram where components light up as messages flow. Visitors can inject failures (fail an inspection, kill a worker mid-pick, poison a message) and watch retries, dead-lettering, and recovery happen live.

**Portfolio story:** event-driven architecture and domain-driven design — reliability engineering made visible.

## Guiding principles

1. **Pipeline-first.** Get a thin, flat-BOM slice flowing end-to-end through RabbitMQ before deepening the domain. Multi-level BOMs, routings, and configure-to-order come after the async backbone works.
2. **Always demoable.** Every milestone leaves the system in a state worth showing. If work stops early, there is still a portfolio piece.
3. **Stories are groomed just-in-time.** Epics are planned up front; story files (N.x.md) are written only when an epic is next up. This keeps the plan honest as earlier work reshapes later epics.

## Epics

| # | Epic | Milestone | Status |
|---|------|-----------|--------|
| 1 | Project foundation | M1 | ✅ Done |
| 2 | Work order domain and lifecycle | M2 | ✅ Done |
| 3 | Core work order and catalog API | M2 | ✅ Done |
| 4 | Messaging infrastructure | M3 | ✅ Done |
| 5 | Material picking workflow | M3 | ✅ Done |
| 6 | Production and inspection workflow | M4 | ✅ Done |
| 7 | Shipping and delivery workflow | M4 | 📋 Groomed — next up |
| 8 | Reliability and recovery | M4 | Planned |
| 9 | Observability | M5 | Planned |
| 10 | Simulation engine | M5 | Planned |
| 11 | Demo dashboard | M5 | Planned |
| 12 | Failure injection | M6 | Planned |
| 13 | Deep domain: multi-level BOMs and routings | M6 | Planned |
| 14 | Testing and quality | M6 | Planned |
| 15 | Deployment and demo operations | M7 | Planned |
| 16 | Documentation and portfolio packaging | M7 | Planned |
| 17 | Stretch improvements | M7+ | Backlog |

## Milestones

- **M1 — Foundation** (done): repo builds, docker infra, CI, migrations.
- **M2 — Synchronous core**: work order domain + REST API, fully tested.
- **M3 — First async slice**: a work order flows API → RabbitMQ → worker → material picking → back, with correlation IDs. *The demo becomes event-driven here.*
- **M4 — Full pipeline + reliability**: production, inspection, shipping stages; retries, DLQ, idempotency. *The happy path and failure paths are complete.*
- **M5 — Visible factory**: observability, simulation engine, React dashboard with live event feed. *The demo becomes watchable.*
- **M6 — Depth**: failure injection, multi-level BOMs, routings, hardened test suite. *The demo becomes impressive.*
- **M7 — Published**: deployed to the home server, embedded in the website, documented and packaged. *The demo becomes public.*

## Provenance

This project began as an e-commerce order processing system (payment → inventory → shipping). It pivoted to manufacturing in mid-2026; the original plan is preserved in git history prior to this commit. The domain model (work orders, state machine, serialized stock) survived the pivot largely intact — evidence the original design kept its concerns well separated.
