## [EPIC] Demo dashboard

**Labels:** epic, frontend, demo
**Milestone:** M5

## Summary

A purpose-built React/TypeScript dashboard: create and follow work orders, watch a live event feed, and see an animated architecture diagram where components light up as messages flow.

## Why

This is the face of the portfolio piece. It's how a visitor — or a hiring manager — experiences the event-driven machinery without reading code.

## Scope

- React + TypeScript SPA, real-time updates via SignalR
- Work order board: orders by pipeline stage, live-updating as events fire
- Order detail / timeline view: every state change, event, pick, build, inspection, and retry for one order (a distributed trace made friendly)
- Live event feed: published/consumed messages streaming as they happen
- Animated architecture diagram: API, broker, workers, DB as a living picture — components pulse as traffic passes through them
- Visitor affordances: create an order from a template, make the hybrid-model decisions (approve schedule, choose spec)

## Acceptance Criteria

- [ ] Dashboard shows the factory state in real time without refresh
- [ ] A visitor can create a work order and watch it travel the whole pipeline
- [ ] The event feed shows real broker traffic as it happens
- [ ] The architecture diagram animates with live activity
- [ ] Works acceptably on a phone (recruiters click links on phones)

## Notes

SignalR hub lives in the API and relays the same event stream the workers consume — one source of truth. Build the board and timeline first; the animated diagram is the showpiece but depends on everything else being stable.
