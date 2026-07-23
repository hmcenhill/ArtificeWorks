import { useRealtime } from "../realtime/RealtimeProvider";
import type { ConnectionStatus as Status } from "../api/realtime";

const LABEL: Record<Status, string> = {
  connecting: "Connecting…",
  connected: "Live",
  reconnecting: "Reconnecting…",
  disconnected: "Offline",
};

const TITLE: Record<Status, string> = {
  connecting: "Connecting to the live event stream",
  connected: "Receiving live factory events",
  reconnecting: "Lost the stream — trying to reconnect",
  disconnected: "Not connected to the live stream",
};

/**
 * The connection's state, in the header. A live demo that silently went dead is worse than one that
 * says so — so this is always visible: green and steady when live, amber and pulsing while trying,
 * red when it has given up.
 */
export function ConnectionStatus() {
  const { status } = useRealtime();
  return (
    <span className={`conn conn-${status}`} role="status" aria-live="polite" title={TITLE[status]}>
      <span className="conn-dot" aria-hidden="true" />
      {LABEL[status]}
    </span>
  );
}
