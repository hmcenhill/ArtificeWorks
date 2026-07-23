import { ApiProblem, type ProblemDetails } from "./problems";
import type {
  CreateWorkOrderBody,
  ProductSummary,
  ShipmentStatus,
  SimulationSettings,
  StockUnit,
  UnitStatus,
  WorkOrder,
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

/**
 * A write (POST/PUT) that speaks RFC 7807 on failure: a non-2xx body is parsed as ProblemDetails
 * and thrown as an {@link ApiProblem}, so the caller can map its `code` to a sentence. Returns the
 * parsed 2xx body, or `undefined` for an empty (204) response.
 */
async function sendJson<T>(
  method: "POST" | "PUT",
  path: string,
  body: unknown,
  headers: Record<string, string> = {},
): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { "Content-Type": "application/json", Accept: "application/json", ...headers },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    let problem: ProblemDetails = {};
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // Non-JSON error body (a proxy 502, say) — the status alone drives the message.
    }
    throw new ApiProblem(response.status, problem);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

// ---- The wire WorkOrderDto serializes its enums as numbers (the name converter is confined to
// the board list DTO, to keep existing API tests' numeric reads green). These arrays decode those
// numbers back to names, ordered to match the domain enums — the one place that brittleness lives.
const STATUS_NAMES: WorkOrderStatus[] = [
  "Intake", "Scheduled", "InProcess", "Inspection", "Delivery", "Completed", "OnHold", "Fault", "Cancelled",
];
const ORIGIN_NAMES: WorkOrderOrigin[] = ["Visitor", "Simulated"];
const UNIT_STATUS_NAMES: UnitStatus[] = ["Built", "Passed", "Scrapped"];
const SHIPMENT_STATUS_NAMES: ShipmentStatus[] = ["Booked", "Dispatched", "Cancelled"];

function decode<T>(names: T[], value: unknown, fallback: T): T {
  return typeof value === "number" && value >= 0 && value < names.length ? names[value] : fallback;
}

/** The raw wire shape of WorkOrderDto, before enum decoding. */
interface RawWorkOrder {
  id: string;
  status: number;
  orderedItemId: string;
  orderItemQty: number;
  origin: number;
  passedQty: number;
  buildAttempt: number;
  units: RawStockUnit[];
  shipment: RawShipment | null;
}
interface RawStockUnit {
  serialNumber: string;
  status: number;
  buildAttempt: number;
  builtUtc: string;
  inspectedUtc: string | null;
  scrapReason: string | null;
}
interface RawShipment {
  carrier: string;
  trackingNumber: string;
  status: number;
  bookedUtc: string;
  estimatedArrivalUtc: string;
  dispatchedUtc: string | null;
  serialNumbers: string[];
}

function mapWorkOrder(raw: RawWorkOrder): WorkOrder {
  return {
    id: raw.id,
    status: decode(STATUS_NAMES, raw.status, "Intake"),
    orderedItemId: raw.orderedItemId,
    orderItemQty: raw.orderItemQty,
    origin: decode(ORIGIN_NAMES, raw.origin, "Visitor"),
    passedQty: raw.passedQty,
    buildAttempt: raw.buildAttempt,
    units: (raw.units ?? []).map(
      (u): StockUnit => ({ ...u, status: decode(UNIT_STATUS_NAMES, u.status, "Built") }),
    ),
    shipment: raw.shipment
      ? { ...raw.shipment, status: decode(SHIPMENT_STATUS_NAMES, raw.shipment.status, "Booked") }
      : null,
  };
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

/** The full order — status, units and shipment — for the detail view's decision moments (11.3). */
export async function fetchWorkOrder(id: string, signal?: AbortSignal): Promise<WorkOrder> {
  return mapWorkOrder(await getJson<RawWorkOrder>(`/work-orders/${id}`, signal));
}

/** The catalog the create form offers as templates (11.3). */
export function fetchProducts(signal?: AbortSignal): Promise<ProductSummary[]> {
  return getJson<ProductSummary[]>("/products", signal);
}

/**
 * Creates a work order and returns it. The `idempotencyKey` (8.4) makes a double-submit — a
 * double-click, a retried network call — resolve to one order: the same key with the same body
 * replays the original 201; with a *different* body it's 422 `idempotency_key_reused`.
 */
export async function createWorkOrder(
  body: CreateWorkOrderBody,
  idempotencyKey: string,
): Promise<WorkOrder> {
  const raw = await sendJson<RawWorkOrder>("POST", "/work-orders", body, {
    "Idempotency-Key": idempotencyKey,
  });
  return mapWorkOrder(raw);
}

// ---- The decision moments. Each is the *ordinary* pipeline endpoint (no dashboard back door):
// a visitor's action is indistinguishable from a simulated one, only tagged Visitor in origin.
// They return the updated order on success and throw ApiProblem on a rejected transition; the
// caller reloads either way, since the SignalR event may not have landed yet.

/** The actor recorded against a visitor's hand-driven action. */
export const VISITOR_ACTOR = "visitor";

export function advanceOrder(id: string, notes?: string): Promise<void> {
  return sendJson("POST", `/work-orders/${id}/advance`, { createdBy: VISITOR_ACTOR, notes });
}

export function holdOrder(id: string, notes?: string): Promise<void> {
  return sendJson("POST", `/work-orders/${id}/hold`, { createdBy: VISITOR_ACTOR, notes });
}

export function releaseOrder(id: string, notes?: string): Promise<void> {
  return sendJson("POST", `/work-orders/${id}/release`, { createdBy: VISITOR_ACTOR, notes });
}

export function cancelOrder(id: string, notes?: string): Promise<void> {
  return sendJson("POST", `/work-orders/${id}/cancel`, { createdBy: VISITOR_ACTOR, notes });
}

/** Records a verdict for one serialized unit — the manual inspector (6.2). */
export function recordVerdict(
  id: string,
  serialNumber: string,
  passed: boolean,
  reason?: string,
): Promise<void> {
  return sendJson("POST", `/work-orders/${id}/inspections`, {
    serialNumber,
    passed,
    reason,
    createdBy: VISITOR_ACTOR,
  });
}

/** Books a carrier — the Delivery decision moment when auto-book is off (7.2). Omit to let the
 *  factory choose one. */
export function bookShipment(id: string, carrier?: string): Promise<void> {
  return sendJson("POST", `/work-orders/${id}/shipments`, {
    carrier: carrier || undefined,
    createdBy: VISITOR_ACTOR,
  });
}

/** The factory's live dials (10.2). */
export function fetchSimulation(signal?: AbortSignal): Promise<SimulationSettings> {
  return getJson<SimulationSettings>("/system/simulation", signal);
}

/**
 * Replaces the dials (10.2). PUT is whole-object, so callers send a full settings body. Returns
 * the applied settings, including the resolved rung, the source, and how long until it takes
 * effect; a 422 `simulation_setting_out_of_range` is thrown and changes nothing.
 */
export function updateSimulation(settings: SimulationSettings): Promise<SimulationSettings> {
  return sendJson<SimulationSettings>("PUT", "/system/simulation", settings);
}
