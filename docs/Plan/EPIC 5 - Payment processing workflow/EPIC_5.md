## [EPIC] Payment processing workflow

**Labels:** epic, messaging, backend
**Milestone:** M3

## Summary

Implement the first async processing stage: payment handling.

## Why

This introduces real event-driven workflow behavior, failure handling, and idempotent consumption.

## Acceptance Criteria

- [ ] Payment consumer processes submitted orders
- [ ] Success path updates order state
- [ ] Failure paths are modeled
- [ ] Duplicate deliveries are handled safely
