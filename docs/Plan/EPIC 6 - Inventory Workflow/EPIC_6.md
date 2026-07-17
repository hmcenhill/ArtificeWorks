## [EPIC] Inventory workflow

**Labels:** epic, messaging, backend
**Milestone:** M4

## Summary

Add inventory reservation as the next stage after successful payment.

## Why

This expands the async pipeline and introduces a second business-critical workflow step.

## Acceptance Criteria

- [ ] Inventory reservation occurs after payment success
- [ ] Insufficient stock is handled cleanly
- [ ] Oversell is prevented under concurrent processing
