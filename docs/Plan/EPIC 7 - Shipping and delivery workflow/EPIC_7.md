## [EPIC] Shipping and delivery workflow

**Labels:** epic, messaging, backend
**Milestone:** M4

## Summary

Schedule shipment of completed units and close out the work order — the happy path's satisfying ending.

## Why

Completes the end-to-end lifecycle, giving the demo a full story to tell and the timeline view its final chapters.

## Scope

- Shipment scheduling after inspection passes (virtual carrier, virtual dates)
- Delivery → Completed transition driven by events
- Shipping failure path defined (e.g., no carrier capacity → OnHold)
- Work order timeline endpoint: the full journey — states, events, picks, builds, inspections, shipment — in one response

## Acceptance Criteria

- [ ] Shipping is requested automatically after successful inspection
- [ ] Successful shipping completes the work order
- [ ] Shipping failure path is defined and recoverable
- [ ] API exposes a clear work order timeline

## Notes

The timeline endpoint is the seed of Epic 11's trace view — design its shape with the dashboard in mind.
