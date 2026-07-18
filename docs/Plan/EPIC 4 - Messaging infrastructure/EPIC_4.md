## [EPIC] Messaging infrastructure

**Labels:** epic, messaging, infra
**Milestone:** M3

## Summary

RabbitMQ publishing from the API and consumption in the Workers service — the async backbone every later workflow stage rides on.

## Why

The event-driven pipeline is the project's core differentiator. This epic delivers the plumbing once, well, so workflow epics (5–7) only add handlers.

## Scope

- Typed event contracts in a shared location (e.g., `WorkOrderScheduled`, `MaterialsPickRequested`)
- Publisher abstraction in Application, RabbitMQ implementation in Infrastructure
- Workers service consumes events with a clean handler-dispatch pattern
- Correlation IDs propagate from API request through every event and log line
- Exchange/queue topology defined in code and documented

## Acceptance Criteria

- [x] App can publish typed events to RabbitMQ
- [x] Workers service consumes events and dispatches to handlers
- [x] Correlation IDs propagate across API → event → worker
- [x] Topology and local broker wiring are documented

## Stories

- [4.1 — Event contracts and publisher](4.1.md)
- [4.2 — Worker consumption and dispatch](4.2.md)
- [4.3 — Correlation and topology documentation](4.3.md)

## Notes

Keep the first version deliberately simple: direct exchanges, manual acks, no retry sophistication — that arrives in Epic 8. The event feed the dashboard needs (Epic 11) will tap this same stream, so design event contracts to be serialization-friendly and self-describing.
