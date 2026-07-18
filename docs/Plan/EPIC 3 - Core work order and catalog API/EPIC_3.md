## [EPIC] Core work order and catalog API

**Labels:** epic, api, backend
**Milestone:** M2
**Status:** ✅ Complete

## Summary

REST endpoints for the product catalog and the full work order lifecycle: create, read, advance, hold/release, and cancel.

## Why

The API is the entry point to the factory and the surface the demo dashboard will drive. Finishing it completes the synchronous core before going async.

## Scope

- Work order create / get / history endpoints *(done)*
- Product create / get endpoints *(done)*
- Lifecycle command endpoints: advance, hold, release
- Cancellation (requires adding a `Cancelled` terminal state to the domain)
- Consistent error contract (ProblemDetails) and integration test coverage

## Acceptance Criteria

- [x] Create and get-by-id endpoints exist for work orders and products
- [x] Lifecycle commands (advance, hold, release) are exposed and guarded
- [x] Cancel endpoint exists with clearly defined allowed states *(3.2)*
- [x] Invalid transitions return a consistent, informative error shape *(3.3 — RFC 7807 ProblemDetails with stable `code`)*
- [x] Core API behavior is covered by integration tests *(3.4 — 68 tests green)*

## Stories

- [3.1 — Lifecycle command endpoints](3.1.md)
- [3.2 — Cancellation](3.2.md)
- [3.3 — Error contract and API polish](3.3.md)
- [3.4 — Integration test coverage](3.4.md)
