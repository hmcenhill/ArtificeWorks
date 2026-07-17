## [EPIC] Reliability and recovery

**Labels:** epic, reliability, backend
**Milestone:** M4

## Summary

Add retries, dead-letter handling, API idempotency, and recovery mechanisms.

## Why

Reliability is the difference between a toy async system and a credible backend portfolio piece.

## Acceptance Criteria

- [ ] Transient failures retry
- [ ] Permanent failures dead-letter cleanly
- [ ] API idempotency keys prevent duplicate effects
- [ ] Failed work can be inspected and optionally reprocessed
