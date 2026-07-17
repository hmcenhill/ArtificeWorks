## [EPIC] Work order domain and lifecycle

**Labels:** epic, domain, backend
**Milestone:** M2
**Status:** ✅ Complete

## Summary

The work order aggregate, its status state machine, and persisted state history — the heart of the factory.

## What was delivered

- `WorkOrder` aggregate: a request to build N units of a `Product`, with serialized `StockKeepingUnit` assignment toward fulfillment
- Status state machine: Intake → Scheduled → InProcess → Inspection → Delivery → Completed, with OnHold (resumable, returns to prior state) and Fault
- Guarded transitions: advance/hold/release commands enforce validity; a superuser `SetStatus` override exists for manual correction
- `WorkOrderStateHistory` records every transition with actor and notes
- EF Core persistence mappings and unit test coverage of the domain rules

## Acceptance Criteria

- [x] Work order aggregate exists
- [x] Valid transitions are enforced
- [x] Invalid transitions are rejected
- [x] Status history is persisted
- [x] Domain rules are covered by tests

## Notes

The Fault state is currently a terminus; Epic 6 (inspection) and Epic 12 (failure injection) will give it real semantics — rework paths, fault reasons, and recovery. A `Cancelled` terminal state is added in Epic 3.
