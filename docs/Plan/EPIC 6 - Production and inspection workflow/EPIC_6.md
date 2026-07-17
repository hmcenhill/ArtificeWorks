## [EPIC] Production and inspection workflow

**Labels:** epic, messaging, backend
**Milestone:** M4

## Summary

The middle of the pipeline: picked work orders move through production (InProcess) and quality inspection, with pass/fail outcomes.

## Why

Production and inspection give the lifecycle its substance and introduce the first *legitimate* failure path — a unit that fails inspection — which the Fault state has been waiting for.

## Scope

- Production consumer: picked work orders enter InProcess; completion is event-driven (timed/simulated for now; the real simulation engine arrives in Epic 10)
- Per-unit build tracking: each `StockKeepingUnit` produced is serialized and assigned to the work order
- Inspection stage: units pass or fail; failures carry a reason
- Fault semantics: define what a failed inspection does to the work order (rework? scrap and rebuild? partial completion?) and record it in state history

## Acceptance Criteria

- [ ] Picked work orders progress through InProcess via events
- [ ] Inspection produces explicit pass/fail outcomes with reasons
- [ ] Failed inspections route the work order to a defined, recoverable path
- [ ] Every stage transition appears in the work order's state history

## Notes

Decide the rework model here deliberately — it's the most interesting domain decision in the pipeline and worth writing up for the portfolio (Epic 16).
