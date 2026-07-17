## [EPIC] Project foundation

**Labels:** epic, infra, devex
**Milestone:** M1
**Status:** ✅ Complete

## Summary

Initial repository structure, local development environment, and delivery pipeline so the project can be built and run consistently.

## What was delivered

- Clean-architecture solution: Domain / Application / Infrastructure / Api / Workers, with Unit and Integration test projects
- Docker Compose stack: PostgreSQL, RabbitMQ (+ management UI), Redis
- ASP.NET Core API bootstrap with Swagger and health checks
- EF Core migrations against PostgreSQL
- GitHub Actions CI running build and tests on push/PR

## Acceptance Criteria

- [x] Repo builds from root
- [x] Local dependencies start with one command
- [x] API starts successfully
- [x] Database migrations apply successfully
- [x] CI runs build and tests on push/PR

## Notes

Completed under the project's original storefront identity; renamed to ArtificeWorks in July 2026. Integration tests use Testcontainers, so a local Docker daemon is required to run the full suite.
