## [EPIC] Deep domain: multi-level BOMs and routings

**Labels:** epic, domain, backend
**Milestone:** M6

## Summary

Deepen the manufacturing model: sub-assemblies with their own BOMs spawning child work orders, and routing steps through work centers.

## Why

This is the DDD showpiece — the shared-platform story from the company pitch made real in the model. It's sequenced late deliberately: the pipeline must already work end-to-end (pipeline-first principle) so this epic deepens rather than destabilizes.

## Scope

- Multi-level BOMs: a component can itself be a manufactured assembly (wheel = rim + spokes + hub); picking a missing sub-assembly spawns a child work order
- Parent/child work order relationships, with parent progress gated on children
- Routings: ordered operation steps (e.g., chassis fab → core install → limb fitting → sensor calibration → final inspection) through named work centers
- Work center capacity as a simple constraint the scheduler and simulation respect
- Shared-platform payoff: BOM explosion view showing component overlap across Custodian / Delver / Courier

## Acceptance Criteria

- [ ] A work order for a product with sub-assemblies spawns and tracks child work orders
- [ ] Parent orders cannot complete before their children
- [ ] Production progresses operation-by-operation through a routing
- [ ] The BOM overlap between product lines is queryable (dashboard-ready)

## Configure-to-order (stretch within this epic)

True CTO — a visitor choosing tool-hands, locomotion, and sensor suite at order time, producing a per-order effective BOM — is planned as the final layer here, only after multi-level BOMs and routings are stable. Until then, "configurations" are just distinct pre-defined products (e.g., "Delver Mk I, mine spec").

## Notes

Product configuration modeling is genuinely hard domain territory. Timebox aggressively; every earlier epic still demos beautifully without this one.
