## [EPIC] Testing and quality

**Labels:** epic, testing
**Milestone:** M6

## Summary

Harden the test suite across the full async system: workflow-level integration tests, contract coverage, and a load smoke test.

## Why

Unit and API tests exist from earlier epics, but an event-driven system's real risks live between services. A public demo also deserves confidence it won't fall over.

## Scope

- End-to-end workflow tests: create → picked → produced → inspected → shipped, through real RabbitMQ (Testcontainers)
- Failure-path tests: insufficient stock, failed inspection, dead-lettering, reprocessing
- Idempotency and duplicate-delivery tests as first-class suites
- Event contract tests so producer/consumer drift is caught in CI
- A simple load smoke test: N concurrent orders, no oversell, no stuck orders
- CI remains fast enough to run on every push

## Acceptance Criteria

- [ ] Happy path is covered by an end-to-end test against real infrastructure
- [ ] Every defined failure path has a test proving its recovery behavior
- [ ] Contract drift between publisher and consumer fails CI
- [ ] Load smoke test passes with zero lost or duplicated work

## Notes

Much of this accretes inside Epics 4–8 as they're built; this epic is the sweep that finds the gaps and raises the floor, not the first time testing happens.
