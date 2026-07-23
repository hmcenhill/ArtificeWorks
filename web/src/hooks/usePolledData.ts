import { useCallback, useEffect, useRef, useState } from "react";

interface PolledData<T> {
  data: T | null;
  error: unknown;
  /** True only on the very first load; a background poll does not flip this. */
  loading: boolean;
  /** True while any fetch (initial or poll) is in flight — for a subtle activity hint. */
  refreshing: boolean;
  /** Fetch now, out of band (the manual refresh button). */
  refresh: () => void;
}

/**
 * Fetches once, then re-fetches every `intervalMs` — the fetched-not-live model 11.1 ships (11.2
 * replaces the interval with a SignalR push). A background poll keeps the last good data on
 * screen, so the board doesn't blank on every tick; only the first load shows a loading state.
 * In-flight requests are aborted on unmount and superseded by the next.
 */
export function usePolledData<T>(
  fetcher: (signal: AbortSignal) => Promise<T>,
  deps: readonly unknown[],
  intervalMs: number,
): PolledData<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  // Keep the latest fetcher without making it a dependency of the effect, so changing closures
  // don't restart the interval; the effect restarts only when the caller's `deps` change.
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;

  const load = useCallback(async (signal: AbortSignal) => {
    setRefreshing(true);
    try {
      const next = await fetcherRef.current(signal);
      if (!signal.aborted) {
        setData(next);
        setError(null);
      }
    } catch (err) {
      if (!signal.aborted && (err as Error)?.name !== "AbortError") {
        setError(err);
      }
    } finally {
      if (!signal.aborted) {
        setLoading(false);
        setRefreshing(false);
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // A nonce lets the manual refresh button force an out-of-band load.
  const [nonce, setNonce] = useState(0);
  const refresh = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    setLoading(true);
    const controller = new AbortController();
    void load(controller.signal);

    const timer = window.setInterval(() => {
      const pollController = new AbortController();
      void load(pollController.signal);
    }, intervalMs);

    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, intervalMs, nonce]);

  return { data, error, loading, refreshing, refresh };
}
