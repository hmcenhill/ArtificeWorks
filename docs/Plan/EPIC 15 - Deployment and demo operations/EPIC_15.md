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
- Deterministic **interview/demo seed**: a script (or command) that resets the DB to a fixed, known starting state — a set of orders at known stages, stable ids — so a live walkthrough has no surprises. Builds on `CatalogSeeder` and `WorldResetService`; distinct from the shared-world reset (which restocks + retires, but does not create a curated scenario).
- Basic uptime monitoring/alerting so a dead demo doesn't sit unnoticed
- Website embedding: the dashboard reachable from (or framed in) the personal site

## Acceptance Criteria

- [ ] Fresh deploy to the home server is scripted/documented and repeatable
- [ ] The demo is reachable over HTTPS from the public internet
- [ ] Abusive traffic is rate-limited; admin surfaces are not publicly writable
- [ ] The world resets on schedule without manual intervention
- [ ] A single command resets the demo to a known, curated starting state for a walkthrough
- [ ] You find out within minutes if the demo goes down

## The exposure topology (settled with the user, 2026-07-23)

One public hostname, one origin, everything behind a reverse proxy on the home server, reached over
the **Cloudflare Tunnel that is already in place** (no port-forwarding, survives the residential IP
changing):

```
Browser ─▶ Cloudflare ─▶ cloudflared tunnel ─▶ reverse proxy ─┬─▶ SPA container (nginx serving dist/)   [ / and static assets ]
                                                              └─▶ API container                          [ /work-orders, /system/*, /hubs/* ]
```

Why this shape, decided while grooming Epic 11:

- **Single origin, so no CORS anywhere.** The dashboard and the API are *not* separate public
  hostnames; the proxy routes paths under one host. This makes production mirror dev (Vite's proxy
  already gives the browser one origin), and it is what makes **SignalR websockets** trivial — a
  cross-origin hub is the fiddliest thing in the stack and this removes it by construction. Epic
  11's SPA therefore calls the API with **root-relative paths only** (`/work-orders`, `/hubs/…`),
  never a hardcoded API host, so the same static bundle drops in behind the proxy untouched.
- **The SPA is its own container** (nginx + the built `dist/`), independently rebuildable, doing its
  own gzip/caching/SPA-fallback — the browser just never sees it as a second origin.
- **Reverse proxy: Caddy is the low-effort pick.** Cloudflare terminates TLS at the edge and the
  tunnel carries traffic to the proxy, so the proxy does pure path routing, not certificates — a
  ~10-line config.
- **Keep SignalR on websockets, not long-polling fallback**, so a live connection isn't a long-lived
  HTTP request fighting Cloudflare's free-plan request timeout (~100s). The app's ordinary requests
  are short and unaffected.

This is Epic 15 work — Epic 11 only has to keep its API paths relative (flagged in 11.1). Adding the
SPA container, the proxy service, and wiring the existing tunnel to it all lands here.

## Notes

Threat-model modestly but honestly: this is a real machine on a home network. Prefer designs where the exposed surface is small and the blast radius of compromise is a disposable container, not the LAN.
