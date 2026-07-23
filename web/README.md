# ArtificeWorks — demo dashboard (`web/`)

The factory made watchable: a Vite + React + TypeScript SPA that shows every live work order in
its pipeline stage and drills into any one order's full timeline. Epic 11.

This app lives **outside** the .NET solution on purpose — a JS app is not a `.csproj`, so keeping
it out leaves `dotnet build` / `dotnet test` and CI untouched. Where the built bundle is finally
served is an Epic 15 decision; this story owes only a static bundle and root-relative API paths.

## Prerequisites

- Node 20+ and npm
- The ArtificeWorks API running locally (see the repo root README / `HANDOFF.md`)

## Run in dev

```bash
# from the repo root, start the API on its HTTP profile (port 5181):
dotnet run --project src/ArtificeWorks.Api --launch-profile http

# in another shell, from web/:
npm install
npm run dev
```

Vite serves the app on <http://localhost:5173> and **proxies** the API's routes
(`/work-orders`, `/products`, `/system/*`, `/hubs/*`) to the API's origin. The browser therefore
makes same-origin requests and there is **no CORS policy** to configure. If your API listens
somewhere other than `http://localhost:5181`, point the proxy at it:

```bash
VITE_API_TARGET=http://localhost:5181 npm run dev
```

The SPA only ever calls the API with **root-relative** paths (`/work-orders`, never
`https://api.…`), so the same bundle works in dev behind the proxy and in production behind a
single reverse proxy, untouched.

## Build

```bash
npm run build     # type-checks, then emits a static bundle to dist/
npm run preview   # serve the built bundle locally
```

`npm run build` produces a self-contained static bundle in `dist/`. Both `node_modules/` and
`dist/` are git-ignored.

## What's here (11.1–11.2)

- **Board** (`/`) — every live order as a card in its stage column (Intake → … → Completed, with
  On Hold / Fault / Cancelled surfaced separately). Visitor vs simulated orders are badged. Since
  11.2 the board is **live over SignalR**: a factory event reloads it (debounced), so a card moves
  between columns on its own — no interval poll. A reconnect reconciles; the manual refresh remains.
- **Order detail** (`/orders/:id`) — the `/work-orders/{id}/timeline` endpoint rendered as one
  chronological column, switched on each entry's `kind` (state / pick / build / inspection /
  verdict / shipment). Live too: an event for *this* order re-fetches it, so it grows as the order
  moves.
- **Live event feed** (11.2) — real broker traffic, newest-first, capped to a rolling window: every
  published `work-order.*` event as it happens, including the `faulted`/`completed` announcements.
  Each line is tagged visitor vs robot when the board knows the order. It is a tail, not a log.
- **Connection status** — always visible in the header (Live / Reconnecting… / Offline): a demo
  that silently went dead is worse than one that says so.

### How realtime works

The API runs a read-only relay (`DashboardRelay`) on its own `artifice.dashboard` queue, bound to
every published routing key, and pushes each event to browsers over the `/hubs/dashboard` SignalR
hub. The client (`src/realtime/RealtimeProvider.tsx`) owns one connection for the whole app with
automatic reconnect. See [docs/messaging-topology.md](../docs/messaging-topology.md) for the broker
side. Dev proxies `/hubs` **with the websocket upgrade** (`ws: true` in `vite.config.ts`).
