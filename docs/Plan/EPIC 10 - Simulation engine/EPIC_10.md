## [EPIC] Simulation engine

**Labels:** epic, simulation, backend
**Milestone:** M5

## Summary

Virtual operators and machines that make the factory run on its own: picks take time, production hums, inspections occasionally fail — without any human driving.

## Why

The demo model is hybrid: visitors create orders and make key decisions; simulated workers do the grunt work. The simulation is what makes the live demo feel alive rather than a form that submits to a database.

## Scope

- Simulated actors (pickers, assemblers, inspectors) that claim and complete pipeline work on realistic, slightly randomized timers
- Tunable behavior: pacing, failure rates, throughput — configurable without redeploy
- Natural failures: some inspections fail, some picks find shelves short, at low configured rates
- Shared-world lifecycle: seed script creates the catalog + initial inventory; a scheduled reset restores the world without downtime
- Background order generation (optional low rate) so the factory is never idle when a visitor arrives

## Acceptance Criteria

- [ ] A created work order progresses through the full pipeline with no human action
- [ ] Simulation pacing and failure rates are configurable at runtime
- [ ] The world can be reseeded/reset on a schedule and on demand
- [ ] Simulated activity is distinguishable in events/logs from visitor actions

## Notes

The simulation rides the same events and consumers as everything else — it is a *client* of the pipeline, not a parallel implementation. If the simulation needs a shortcut the pipeline doesn't offer, that's a smell worth examining.
