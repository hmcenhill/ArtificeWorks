## [EPIC] Stretch improvements

**Labels:** epic, stretch
**Milestone:** M7+

## Summary

Optional deepening after the core system is live, documented, and demoable. Backlog, not commitment.

## Candidate scope

- Transactional outbox pattern (publish-and-persist atomicity — a strong senior-level talking point)
- Formal saga/process manager for the pipeline instead of implicit choreography
- Configure-to-order UI: visitors spec their own automaton (see Epic 13 stretch)
- Multiple warehouses / factory locations
- AuthN/Z with an admin role replacing the current guarded-endpoint approach
- Cloud deployment variant (translating the self-hosted setup to Azure)
- Event sourcing for the work order aggregate

## Acceptance Criteria

- [ ] Core project is already live and documented before any stretch work starts
- [ ] Stretch work never displaces demo stability, documentation, or testing

## Notes

Each item should only be pulled when it has a clear portfolio payoff to justify its cost. It is fine for this list to remain mostly unbuilt.
