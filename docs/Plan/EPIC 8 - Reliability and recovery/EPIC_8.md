## [EPIC] Reliability and recovery

**Labels:** epic, reliability, backend
**Milestone:** M4

## Summary

Retries, dead-letter handling, API idempotency, and recovery mechanisms across the whole pipeline.

## Why

Reliability is the difference between a toy async system and a credible backend portfolio piece — and it's the machinery Epic 12's failure injection will put on stage.

## Scope

- Transient failure retry policy (with backoff) on consumers
- Dead-letter queues for permanent failures, with inspection tooling
- API idempotency keys so client retries can't double-create work orders
- Reprocessing: failed/dead-lettered work can be examined and requeued
- Poison message handling: a malformed message can't wedge a consumer

## Acceptance Criteria

- [ ] Transient failures retry with backoff and eventually succeed or dead-letter
- [ ] Permanent failures dead-letter cleanly without blocking the queue
- [ ] API idempotency keys prevent duplicate effects
- [ ] Failed work can be inspected and optionally reprocessed via API

## Notes

Everything built here should emit events/metrics about itself — retries and dead-letters are exactly what the demo wants to make visible later. Design for an audience.
