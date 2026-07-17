## [EPIC] Failure injection

**Labels:** epic, demo, reliability
**Milestone:** M6

## Summary

Visitor-triggered chaos: fail this inspection, kill a worker mid-pick, poison a message — then watch the reliability machinery from Epic 8 recover, live on the dashboard.

## Why

This is the money shot for an event-driven portfolio. Anyone can claim "handles failures gracefully"; this demo *shows* it, on demand, to a stranger with a mouse.

## Scope

- Failure injection API (guarded, rate-limited): per-order failure flags and system-level chaos actions
- Fail an inspection: the targeted order's inspection fails, visibly routing to the fault/rework path
- Kill a worker mid-task: a consumer dies while holding work; the message redelivers and another consumer completes it
- Poison message: an unprocessable message retries, then dead-letters — visible in the feed and DLQ view
- Dashboard integration: chaos buttons in context, plus a DLQ inspector showing dead-lettered work and offering reprocess

## Acceptance Criteria

- [ ] Each injected failure produces a visibly correct recovery on the dashboard
- [ ] Injected chaos cannot corrupt the shared world (bounded blast radius, world reset covers the rest)
- [ ] Dead-lettered work is visible and reprocessable from the dashboard
- [ ] Failure injection is rate-limited so visitors can't grief each other

## Notes

Scope chaos to the order the visitor is viewing wherever possible — shared-world courtesy. This epic is mostly *wiring existing reliability into the demo*; if Epic 8 was built well, this epic is small and delightful.
