## [EPIC] Documentation and portfolio packaging

**Labels:** epic, docs
**Milestone:** M7

## Summary

Package ArtificeWorks so someone can understand, run, and evaluate it quickly — and so it sells its builder.

## Why

Good packaging turns a private learning project into a professional portfolio asset. The audience is a hiring manager with ten minutes.

## Scope

- Polished README: the pitch, a screenshot/GIF of the dashboard, quickstart, architecture at a glance
- Architecture documentation: system diagram, event catalog, and the "under the hood" explainers the website needs
- Reliability strategy write-up: retries, DLQ, idempotency, and the design decisions behind them (including the rework-model decision from Epic 6)
- Demo script: the guided five-minute tour, including which failures to inject and what to watch
- Project narrative and resume bullets — including the AI-directed development story: how the project was built by directing an AI coding agent while maintaining full command of the design

## Acceptance Criteria

- [ ] A stranger can go from clone to running system with only the README
- [ ] Architecture and reliability docs exist and match reality
- [ ] Demo script produces a reliably impressive five minutes
- [ ] Resume bullets and project narrative are drafted

## Notes

Write toward the website: every doc here should be publishable as (or easily adapted into) the explainer content that accompanies the live demo.
