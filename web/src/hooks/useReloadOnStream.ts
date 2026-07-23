import { useEffect, useRef } from "react";

import type { DashboardEvent } from "../api/realtime";
import { useRealtime } from "../realtime/RealtimeProvider";

/**
 * Bridges the SignalR stream to a {@link useLiveData} `reload`. Reloads (debounced, so a burst of
 * events collapses into one fetch) whenever a relevant event arrives, and once more each time the
 * connection re-establishes — the reconnect reconciliation that catches whatever was missed while
 * the socket was down. `relevant` decides which events matter: every event for the board, one
 * order for the detail view.
 */
export function useReloadOnStream(
  reload: () => void,
  relevant: (event: DashboardEvent) => boolean,
  debounceMs = 300,
) {
  const { subscribe, status } = useRealtime();

  const reloadRef = useRef(reload);
  reloadRef.current = reload;
  const relevantRef = useRef(relevant);
  relevantRef.current = relevant;

  useEffect(() => {
    let timer: number | undefined;
    const unsubscribe = subscribe((event) => {
      if (!relevantRef.current(event)) return;
      window.clearTimeout(timer);
      timer = window.setTimeout(() => reloadRef.current(), debounceMs);
    });
    return () => {
      window.clearTimeout(timer);
      unsubscribe();
    };
  }, [subscribe, debounceMs]);

  // Reconnect reconciliation: on the transition back to connected, refetch once to catch up.
  const previous = useRef(status);
  useEffect(() => {
    if (status === "connected" && previous.current !== "connected") {
      reloadRef.current();
    }
    previous.current = status;
  }, [status]);
}
