import { useCallback, useEffect, useRef, useState } from "react";

interface LiveData<T> {
  data: T | null;
  error: unknown;
  /** True only on the very first load; a reload keeps the last good data on screen. */
  loading: boolean;
  /** True while any fetch is in flight — for a subtle activity hint. */
  refreshing: boolean;
  /** Fetch now. The board/detail wire this to a SignalR event and to a reconnect. */
  reload: () => void;
}

/**
 * Fetches once, then re-fetches only when told to (11.2's push model, replacing 11.1's interval
 * poll). The board reloads on a factory event and on reconnect; there is no timer. A reload keeps
 * the last good data visible, so the view never blanks mid-stream; only the first load shows a
 * loading state. In-flight requests are aborted on unmount and superseded by the next.
 */
export function useLiveData<T>(
  fetcher: (signal: AbortSignal) => Promise<T>,
  deps: readonly unknown[],
): LiveData<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  // Keep the latest fetcher without making it an effect dependency, so a changing closure doesn't
  // re-run the initial load; the effect re-runs only when the caller's `deps` change.
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
  }, []);

  // A nonce forces an out-of-band reload.
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    // Only the initial load (deps change) shows the loading state; a reload (nonce) does not.
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps]);

  useEffect(() => {
    if (nonce === 0) return;
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nonce]);

  return { data, error, loading, refreshing, reload };
}
