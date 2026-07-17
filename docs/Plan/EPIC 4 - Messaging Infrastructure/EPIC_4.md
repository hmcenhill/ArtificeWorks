## [EPIC] Messaging infrastructure

**Labels:** epic, messaging, infra
**Milestone:** M3

## Summary

Add RabbitMQ publishing and worker consumption to support asynchronous order processing.

## Why

The async workflow is the main differentiator between this project and basic CRUD.

## Acceptance Criteria

- [ ] App can publish typed events
- [ ] Worker can consume events
- [ ] Correlation IDs propagate across stages
- [ ] Local broker wiring is documented
