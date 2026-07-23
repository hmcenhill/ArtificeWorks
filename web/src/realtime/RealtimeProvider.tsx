import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";

import {
  createConnection,
  statusOf,
  WORK_ORDER_EVENT,
  type ConnectionStatus,
  type DashboardEvent,
} from "../api/realtime";
import type { WorkOrderListItem, WorkOrderOrigin } from "../api/types";

// The feed is a tail, not a log: the dead-letter view and traces are where history lives. Cap it so
// a long-running demo doesn't grow an unbounded list in memory or on screen.
const FEED_CAP = 60;

export interface FeedEntry extends DashboardEvent {
  /** Resolved from board data when the order is known — the relay can't know origin (it reads no DB). */
  origin: WorkOrderOrigin | null;
  /** Stable list key: eventId can repeat (publishing is at-least-once), so a sequence disambiguates. */
  key: string;
}

interface RealtimeContextValue {
  status: ConnectionStatus;
  feed: FeedEntry[];
  /** React to every incoming event; returns an unsubscribe. */
  subscribe: (handler: (event: DashboardEvent) => void) => () => void;
  /** Teach the feed which orders are visitor vs robot, from whatever the board has loaded. */
  noteOrigins: (items: WorkOrderListItem[]) => void;
}

const RealtimeContext = createContext<RealtimeContextValue | null>(null);

/**
 * Owns the single SignalR connection for the whole app (11.2), so it survives route changes and one
 * factory event reaches the board, the open detail and the feed at once. Surfaces connection state —
 * a live demo that silently went dead is worse than one that says "reconnecting" — and keeps the
 * rolling feed buffer, tagging each line with the order's origin when the board has told us.
 */
export function RealtimeProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  const [feed, setFeed] = useState<FeedEntry[]>([]);

  const subscribersRef = useRef(new Set<(event: DashboardEvent) => void>());
  const originsRef = useRef(new Map<string, WorkOrderOrigin>());
  const seqRef = useRef(0);

  const subscribe = useCallback((handler: (event: DashboardEvent) => void) => {
    subscribersRef.current.add(handler);
    return () => {
      subscribersRef.current.delete(handler);
    };
  }, []);

  const noteOrigins = useCallback((items: WorkOrderListItem[]) => {
    let added = false;
    for (const item of items) {
      if (originsRef.current.get(item.id) !== item.origin) {
        originsRef.current.set(item.id, item.origin);
        added = true;
      }
    }
    // Backfill feed lines whose origin we didn't know when the event first arrived.
    if (added) {
      setFeed((prev) => {
        let changed = false;
        const next = prev.map((entry) => {
          if (entry.origin != null || !entry.workOrderId) return entry;
          const origin = originsRef.current.get(entry.workOrderId);
          if (!origin) return entry;
          changed = true;
          return { ...entry, origin };
        });
        return changed ? next : prev;
      });
    }
  }, []);

  useEffect(() => {
    let disposed = false;
    const connection = createConnection();

    connection.on(WORK_ORDER_EVENT, (event: DashboardEvent) => {
      const origin = event.workOrderId ? originsRef.current.get(event.workOrderId) ?? null : null;
      const key = `${event.eventId}#${seqRef.current++}`;
      setFeed((prev) => [{ ...event, origin, key }, ...prev].slice(0, FEED_CAP));
      subscribersRef.current.forEach((handler) => handler(event));
    });

    connection.onreconnecting(() => !disposed && setStatus("reconnecting"));
    connection.onreconnected(() => !disposed && setStatus("connected"));
    connection.onclose(() => !disposed && setStatus("disconnected"));

    setStatus("connecting");
    connection
      .start()
      .then(() => !disposed && setStatus(statusOf(connection)))
      .catch(() => !disposed && setStatus("disconnected"));

    return () => {
      disposed = true;
      void connection.stop();
    };
  }, []);

  const value = useMemo<RealtimeContextValue>(
    () => ({ status, feed, subscribe, noteOrigins }),
    [status, feed, subscribe, noteOrigins],
  );

  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>;
}

export function useRealtime(): RealtimeContextValue {
  const context = useContext(RealtimeContext);
  if (!context) {
    throw new Error("useRealtime must be used within a RealtimeProvider.");
  }
  return context;
}
