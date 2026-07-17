## [EPIC] Deployment and demo operations

**Labels:** epic, infra, ops
**Milestone:** M7

## Summary

Run the whole system on the home server, exposed safely to the internet, embedded in the personal website, and able to survive strangers.

## Why

A demo that only runs on localhost isn't a demo. Self-hosting is also its own resume story: TLS, reverse proxying, hardening, and unattended operation.

## Scope

- Production compose profile (or equivalent) for the full stack on the home server
- Safe exposure: reverse proxy, TLS, and no direct exposure of home network internals (consider a tunnel, e.g., Cloudflare Tunnel)
- Public-facing hardening: rate limiting, request size limits, admin/chaos endpoints gated
- Shared-world reset job scheduled (nightly) and triggerable manually
- Basic uptime monitoring/alerting so a dead demo doesn't sit unnoticed
- Website embedding: the dashboard reachable from (or framed in) the personal site

## Acceptance Criteria

- [ ] Fresh deploy to the home server is scripted/documented and repeatable
- [ ] The demo is reachable over HTTPS from the public internet
- [ ] Abusive traffic is rate-limited; admin surfaces are not publicly writable
- [ ] The world resets on schedule without manual intervention
- [ ] You find out within minutes if the demo goes down

## Notes

Threat-model modestly but honestly: this is a real machine on a home network. Prefer designs where the exposed surface is small and the blast radius of compromise is a disposable container, not the LAN.
