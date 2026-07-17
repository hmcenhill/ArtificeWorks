## [EPIC] Material picking workflow

**Labels:** epic, messaging, backend, domain
**Milestone:** M3

## Summary

The first async pipeline stage: when a work order is scheduled, reserve and pick the component materials its product's bill of materials calls for.

## Why

This is where the factory starts behaving like a factory — and where the classic event-driven problems (idempotent consumption, reservation under concurrency) first appear. It absorbs the roles that payment *and* inventory reservation played in the original storefront plan.

## Scope

- Flat, single-level BOM on `Product`: a list of component materials and quantities (multi-level comes in Epic 13)
- On-hand inventory for component materials
- Picking consumer: on `WorkOrderScheduled`, reserve components per the BOM and record the pick
- Insufficient stock places the work order OnHold with a reason, releasing nothing it didn't reserve
- No double-allocation of the same stock under concurrent picking
- Duplicate event deliveries are handled safely (idempotent consumption)

## Acceptance Criteria

- [ ] Products carry a flat BOM; seed data includes the shared-platform catalog (chassis, power core, control stack, tool-hands, locomotion, sensors)
- [ ] A scheduled work order triggers material reservation via an event, not an API call
- [ ] Insufficient stock is handled cleanly and visibly (OnHold + reason in state history)
- [ ] Concurrent work orders cannot reserve the same stock twice
- [ ] Duplicate deliveries of the same event have no additional effect

## Notes

Seed the catalog so the Custodian, Delver, and Courier share most components — the "70% shared BOM" from the company pitch should be literally true in the data, because Epic 11's dashboard will visualize it.
