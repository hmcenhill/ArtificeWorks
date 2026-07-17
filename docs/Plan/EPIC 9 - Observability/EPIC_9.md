## [EPIC] Observability

**Labels:** epic, observability, infra
**Milestone:** M5

## Summary

Structured logging, metrics, and tracing so one work order can be followed through every stage of the pipeline.

## Why

Observability makes the async workflow understandable — for debugging, and as the raw material the demo dashboard turns into visuals.

## Scope

- Structured logging with correlation IDs on every log line, API and workers alike
- Metrics: throughput, failure counts, retry counts, queue depths per stage
- Distributed tracing (OpenTelemetry) spanning API → broker → worker
- A documented way to answer "what happened to work order X?" end to end

## Acceptance Criteria

- [ ] Correlation IDs are logged consistently across services
- [ ] Metrics expose throughput/failure/retry counts per pipeline stage
- [ ] One work order can be traced through multiple stages
- [ ] Local setup for viewing logs/metrics/traces is documented

## Notes

Prefer OpenTelemetry conventions — resume-relevant and tool-agnostic. The dashboard (Epic 11) consumes the same signals; don't build bespoke plumbing it can't reuse.
