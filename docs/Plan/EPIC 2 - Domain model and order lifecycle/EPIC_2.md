## [EPIC] Domain model and order lifecycle

**Labels:** epic, domain, backend
**Milestone:** M2

## Summary

Design the order domain model, status transitions, and persistence of order history.

## Why

The domain model is the center of the project and should clearly express valid workflow behavior.

## Scope

- Order aggregate
- Order items
- State machine
- Status history
- Persistence mappings

## Acceptance Criteria

- [x] Order aggregate exists
- [x] Valid transitions are enforced
- [x] Invalid transitions are rejected
- [x] Status history is persisted
- [x] Domain rules are covered by tests
