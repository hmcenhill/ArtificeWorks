import type {
  WorkOrderListItem,
  WorkOrderOrigin,
  WorkOrderStatus,
  WorkOrderTimeline,
} from "./types";

// Root-relative paths only — never a hardcoded host. In dev Vite proxies these to the API; in
// production they are same-origin behind one reverse proxy. The same bundle works in both.

/** An API call that returned a non-2xx status. Carries the status so views can tell 404 apart. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    headers: { Accept: "application/json" },
    signal,
  });
  if (!response.ok) {
    throw new ApiError(response.status, `GET ${path} failed: ${response.status}`);
  }
  return (await response.json()) as T;
}

export interface WorkOrderListQuery {
  status?: WorkOrderStatus[];
  origin?: WorkOrderOrigin[];
  limit?: number;
}

function buildQuery(query: WorkOrderListQuery): string {
  const params = new URLSearchParams();
  // status and origin are repeatable — one query param per value.
  query.status?.forEach((s) => params.append("status", s));
  query.origin?.forEach((o) => params.append("origin", o));
  if (query.limit != null) {
    params.set("limit", String(query.limit));
  }
  const q = params.toString();
  return q ? `?${q}` : "";
}

/** The board read model: the factory's current orders, filtered, bounded and newest-first. */
export function fetchWorkOrders(
  query: WorkOrderListQuery = {},
  signal?: AbortSignal,
): Promise<WorkOrderListItem[]> {
  return getJson<WorkOrderListItem[]>(`/work-orders${buildQuery(query)}`, signal);
}

/** One order's whole story, derived from the records the system keeps (7.4). */
export function fetchTimeline(
  id: string,
  signal?: AbortSignal,
): Promise<WorkOrderTimeline> {
  return getJson<WorkOrderTimeline>(`/work-orders/${id}/timeline`, signal);
}
