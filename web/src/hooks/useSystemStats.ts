import { useEffect, useState } from "react";

import { fetchStats } from "../api/client";
import type { SystemStats } from "../api/types";

/**
 * Slow-polls GET /system/stats for the architecture diagram's strain tint (11.4). Deliberately a
 * timer, not the SignalR stream: the stats are an *aggregate* snapshot (cached server-side), so a
 * per-event refetch would be wasted work — the story asks for a low-frequency poll. A transient
 * failure keeps the last good numbers on screen rather than blanking the colour. Skips fetching
 * while the tab is hidden — a diagram left on a demo screen shouldn't poll a backgrounded tab — and
 * aborts the in-flight request and clears the timer on unmount, so nothing leaks.
 */
export function useSystemStats(intervalMs = 5000): SystemStats | null {
  const [stats, setStats] = useState<SystemStats | null>(null);

  useEffect(() => {
    let cancelled = false;
    let controller: AbortController | null = null;

    const tick = async () => {
      if (document.hidden) return;
      controller?.abort();
      controller = new AbortController();
      try {
        const next = await fetchStats(controller.signal);
        if (!cancelled) setStats(next);
      } catch {
        // Transient (a blip, an abort) — hold the last snapshot; the next tick will catch up.
      }
    };

    void tick();
    const id = window.setInterval(tick, intervalMs);
    // Catch up promptly when the tab comes back rather than waiting out the interval.
    const onVisible = () => !document.hidden && void tick();
    document.addEventListener("visibilitychange", onVisible);

    return () => {
      cancelled = true;
      controller?.abort();
      window.clearInterval(id);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [intervalMs]);

  return stats;
}
