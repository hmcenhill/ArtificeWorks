## [EPIC] Project foundation

**Labels:** epic, infra, devex
**Milestone:** M1

## Summary

Create the initial repository structure, local development environment, and delivery pipeline so the project can be built and run consistently.

## Why

A reliable foundation reduces friction and makes every later feature easier to implement and test.

## Scope

- Solution structure
- Local Docker infrastructure
- API bootstrap
- Database migrations
- CI pipeline

## Acceptance Criteria

- [x] Repo builds from root
- [x] Local dependencies start with one command
- [x] API starts successfully
- [x] Database migrations apply successfully
- [x] CI runs build and tests on push/PR

## Notes

This epic is complete when a fresh clone can be built and started without manual setup beyond documented prerequisites.
