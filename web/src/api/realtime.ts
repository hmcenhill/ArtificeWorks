import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";

// The realtime channel: a SignalR connection to the API's dashboard hub (11.2). The relay on the
// API pushes one message — `WorkOrderEvent` — for every work-order.* event the factory publishes,
// so the board, the detail and the feed can react without polling.

/** Mirrors Api.Realtime.DashboardEvent. Metadata only — the board still reads authoritative state
 *  from GET /work-orders, so this carries just enough to know *what* changed and *which* order. */
export interface DashboardEvent {
  eventId: string;
  eventType: string;
  correlationId: string;
  occurredUtc: string;
  workOrderId: string | null;
}

/** The client method the hub invokes. Matches IDashboardClient.WorkOrderEvent on the server. */
export const WORK_ORDER_EVENT = "WorkOrderEvent";

/** The hub route. Root-relative — Vite proxies /hubs through with the websocket upgrade in dev. */
export const HUB_URL = "/hubs/dashboard";

/**
 * A connection-state summary the UI can render, collapsed from SignalR's lifecycle: the demo cares
 * only whether it is live, trying, or gave up — not the exact transport phase.
 */
export type ConnectionStatus = "connecting" | "connected" | "reconnecting" | "disconnected";

export function statusOf(connection: HubConnection): ConnectionStatus {
  switch (connection.state) {
    case HubConnectionState.Connected:
      return "connected";
    case HubConnectionState.Reconnecting:
      return "reconnecting";
    case HubConnectionState.Connecting:
      return "connecting";
    default:
      return "disconnected";
  }
}

/**
 * Builds the hub connection with automatic reconnect. The retry delays climb then hold, so a demo
 * that briefly loses the API keeps trying without hammering it. Starting it — and surfacing state —
 * is the provider's job; this only wires the shape.
 */
export function createConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(HUB_URL)
    // Climb, then hold at 10s: reconnect quickly after a blip, patiently after a longer outage.
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 10000])
    .configureLogging(LogLevel.Warning)
    .build();
}
